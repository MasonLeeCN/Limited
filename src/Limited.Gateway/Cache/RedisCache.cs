﻿using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Limited.Gateway.Cache
{
    /// <summary>
    /// 缓存链接对象
    /// </summary>
    sealed class CacheConnection
    {
        private static CacheConnection instance = null;
        private static ConnectionMultiplexer Connection = null;
        private static readonly SemaphoreSlim _connectionLock = new SemaphoreSlim(1, 1);

        private static string ConnectionString
        {
            get
            {
                //读取配置文件
                var configBuilder = new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory()).AddJsonFile("appsettings.json");
                var config = configBuilder.Build();
                var connString = config.GetSection("ConnectionString:Cache").Value;
                if (connString == null)
                {
                    throw new Exception("ConnectionString:Cache is undefined");
                }

                return connString;
            }
        }

        public IDatabase Database { get; set; } = null;

        /// <summary>
        /// 缓存链接单例,
        /// </summary>
        /// <returns></returns>
        public static CacheConnection CreateInstance()
        {
            if (instance == null)
            {
                _connectionLock.Wait();
                try
                {
                    if (instance == null)
                    {
                        //设置比较大的线程池,据说能避免Timeout 陷阱
                        ThreadPool.SetMaxThreads(100, 100);
                        instance = new CacheConnection();
                        Connection = ConnectionMultiplexer.Connect(ConnectionString);
                        Connection.PreserveAsyncOrder = false;
                        instance.Database = Connection.GetDatabase(-1, null);
                    }
                }
                finally
                {
                    _connectionLock.Release();
                }
            }
            else
            {
                if (Connection == null || !Connection.IsConnected)
                {
                    _connectionLock.Wait();
                    try
                    {
                        if (Connection == null || !Connection.IsConnected)
                        {
                            ThreadPool.SetMaxThreads(100, 100);
                            Connection = ConnectionMultiplexer.Connect(ConnectionString);
                            Connection.PreserveAsyncOrder = false;
                            instance.Database = Connection.GetDatabase(-1, null);
                        }
                    }
                    finally
                    {
                        _connectionLock.Release();
                    }
                }
            }
            return instance;
        }
    }

    public class RedisCache : ICache
    {
        public async Task<bool> Exists(string key)
        {
            var db = CacheConnection.CreateInstance().Database;
            return await db.KeyExistsAsync(key);
        }

        public async Task<string> Get(string key)
        {
            var db = CacheConnection.CreateInstance().Database;
            var result = await db.StringGetAsync(key);
            return result;
        }

        public async Task<List<string>> Get(List<string> keys)
        {
            var list = await Task.Run(() =>
            {
                var db = CacheConnection.CreateInstance().Database;
                var batch = db.CreateBatch();

                List<Task<RedisValue>> tasks = new List<Task<RedisValue>>();

                for (var m = 0; m < keys.Count; m++)
                {
                    tasks.Add(batch.StringGetAsync(keys[m]));
                }
                batch.Execute();

                Task.WhenAll(tasks.ToArray());

                var result = new List<string>();
                foreach (var task in tasks)
                {
                    result.Add(task.Result.ToString());
                }

                return result;
            });

            return list;
        }

        public async Task<Dictionary<string, string>> GetHash(string key)
        {
            var items = new Dictionary<string, string>();

            var db = CacheConnection.CreateInstance().Database;
            var result = await db.HashGetAllAsync(key);
            var kvps = result.ToDictionary();

            if (kvps.Count == 0)
            {
                return default;
            }

            foreach (var kvp in kvps)
            {
                items.Add(kvp.Key, kvp.Value);
            }

            //如果hash结构的字段设置了过期时间,则数据过期后,直接删除该节点
            if (kvps.ContainsKey("_ExpiryTime_"))
            {
                DateTime expTime = default;

                if (!DateTime.TryParse(kvps["_ExpiryTime_"].ToString(), out expTime))
                {
                    return default;
                }
                if (expTime < DateTime.Now)
                {
                    db.KeyDelete(key);
                    return default;
                }
                return items;
            }
            else
            {
                return items;
            }
        }

        public async Task<string> GetHash(string key, string field)
        {
            var items = new Dictionary<string, string>();
            var db = CacheConnection.CreateInstance().Database;
            var expTimeStr = await db.HashGetAsync(key, "_ExpiryTime_");
            var value = await db.HashGetAsync(key, field);

            if (!expTimeStr.IsNullOrEmpty)
            {
                var expTime = default(DateTime);
                if (!DateTime.TryParse(expTimeStr.ToString(), out expTime))
                {
                    db.KeyDelete(key);
                    return default;
                }
                if (expTime < DateTime.Now)
                {
                    db.KeyDelete(key);
                    return default;
                }
                return value.ToString();
            }
            return value.ToString();
        }

        public async Task<List<string>> GetHash(List<string> keys, string field)
        {
            var db = CacheConnection.CreateInstance().Database;
            var batch = db.CreateBatch();

            var tasks = new List<Task<HashEntry[]>>();

            foreach (var key in keys)
            {
                tasks.Add(batch.HashGetAllAsync(key));
            }

            batch.Execute();
            await Task.WhenAll(tasks.ToArray());

            var result = new List<string>();

            for (var m = 0; m < tasks.Count; m++)
            {
                var kvps = tasks[m].Result.ToDictionary();

                if (kvps.Count == 0)
                {
                    result.Add(default);
                    continue;
                }

                var items = new Dictionary<string, string>();
                foreach (var kvp in kvps)
                {
                    items.Add(kvp.Key, kvp.Value);
                }

                if (kvps.ContainsKey("_ExpiryTime_"))
                {
                    var expTime = default(DateTime);

                    if (!DateTime.TryParse(kvps["_ExpiryTime_"].ToString(), out expTime))
                    {
                        result.Add(default);
                        continue;
                    }
                    if (expTime < DateTime.Now)
                    {
                        result.Add(default);
                        continue;
                    }
                }

                if (!kvps.ContainsKey(field))
                {
                    result.Add(default);
                    continue;
                }
                result.Add(kvps[field].ToString());
            }


            var removeKeys = new List<string>();
            for (var m = 0; m < result.Count; m++)
            {
                if (result[m] == default)
                {
                    removeKeys.Add(keys[m]);
                }
            }
            Remove(removeKeys);

            return result;
        }

        public async Task<bool> Remove(string key)
        {
            var db = CacheConnection.CreateInstance().Database;
            return await db.KeyDeleteAsync(key);
        }

        public async Task<List<bool>> Remove(List<string> keys)
        {
            var db = CacheConnection.CreateInstance().Database;
            var batch = db.CreateBatch();

            var tasks = new List<Task<bool>>();

            foreach (var key in keys)
            {
                batch.KeyDeleteAsync(key);
            }
            batch.Execute();

            Task.WaitAll(tasks.ToArray());

            var result = new List<bool>();
            foreach (var t in tasks)
            {
                result.Add(t.Result);
            }

            return result;
        }

        public async Task<bool> Set<T>(CacheNode<T> node)
        {
            var db = CacheConnection.CreateInstance().Database;
            var json = JsonConvert.SerializeObject(node.Data);

            if (node.CacheTime != default)
            {
                return await db.StringSetAsync(node.Key, json, node.CacheTime);
            }
            else
            {
                return await db.StringSetAsync(node.Key, json);
            }
        }

        public async Task<bool> Set<T>(List<CacheNode<T>> nodes)
        {
            try
            {
                var db = CacheConnection.CreateInstance().Database;
                var batch = db.CreateBatch();

                var tasks = new List<Task<bool>>();
                for (var m = 0; m < nodes.Count; m++)
                {
                    var json = JsonConvert.SerializeObject(nodes[m].Data);
                    if (nodes[m].CacheTime != default(TimeSpan))
                    {
                        tasks.Add(batch.StringSetAsync(nodes[m].Key, json, nodes[m].CacheTime));
                    }
                    else
                    {
                        tasks.Add(batch.StringSetAsync(nodes[m].Key, json));
                    }
                }

                batch.Execute();
                Task.WaitAll(tasks.ToArray());
                return true;
            }
            catch (Exception exp)
            {
                return false;
            }
        }

        public async Task<bool> SetHash<T>(CacheNode<T> node)
        {
            Task<bool> task = Task.Factory.StartNew(() =>
            {
                try
                {
                    var db = CacheConnection.CreateInstance().Database;
                    var batch = db.CreateBatch();
                    var maps = ToMap(node.Data);

                    if (node.CacheTime != default)
                    {
                        var currentTime = DateTime.Now;
                        maps.Add("_ExpiryTime_", currentTime.Add(node.CacheTime).ToString());
                    }

                    foreach (var map in maps)
                    {
                        batch.HashSetAsync(node.Key, map.Key, map.Value);
                    }

                    batch.Execute();

                    return true;
                }
                catch (Exception exp)
                {
                    return false;
                }
            });
            await Task.WhenAll(task);
            return task.Result;
        }

        public async Task<bool> SetHash<T>(List<CacheNode<T>> nodes)
        {
            if (nodes.Count > 65535)
            {
                throw new Exception("Hi guys, i can't digest all those data at one time! may be you can try 'SetAsync' ");
            }

            Task<bool> task = Task.Factory.StartNew(() => 
            {

                try
                {
                    var db = CacheConnection.CreateInstance().Database;
                    var batch = db.CreateBatch();
                    var currentTime = DateTime.Now;
                    for (var m = 0; m < nodes.Count; m++)
                    {
                        var maps = ToMap(nodes[m].Data);

                        if (nodes[m].CacheTime != default(TimeSpan))
                        {
                            maps.Add("_ExpiryTime_", currentTime.Add(nodes[m].CacheTime).ToString());
                        }

                        foreach (var map in maps)
                        {
                            batch.HashSetAsync(nodes[m].Key, map.Key, map.Value);
                        }
                    }
                    batch.Execute();
                    return true;
                }
                catch (Exception exp)
                {
                    return false;
                }
            });

             task.Wait();

            return task.Result;
        }

        private Dictionary<string, string> ToMap(Object o)
        {
            Dictionary<string, string> map = new Dictionary<string, string>();

            Type t = o.GetType();

            PropertyInfo[] pi = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);

            foreach (PropertyInfo p in pi)
            {
                MethodInfo mi = p.GetGetMethod();

                if (mi != null && mi.IsPublic)
                {
                    var value = mi.Invoke(o, new Object[] { });

                    var valueString = string.Empty;
                    if (value is ValueType || value is string)
                    {
                        valueString = value.ToString();
                    }
                    else
                    {
                        valueString = JsonConvert.SerializeObject(value);
                    }

                    map.Add(p.Name, valueString);
                }
            }

            return map;
        }
    }
}