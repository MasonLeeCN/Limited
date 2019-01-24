﻿using Limited.MicroService;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DownstreamService
{
    public class Startup
    {
        public IConfiguration Configuration { get; }


        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddMvc().SetCompatibilityVersion(CompatibilityVersion.Version_2_1);
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env, IApplicationLifetime lifetime)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseHsts();
            }

            app.UseMicroService(lifetime, (service) =>
            {
                service.Name = "Api";
                service.DisplayName = "订单服务";
                service.Version = "1.0";
                service.XmlName = "DownstreamService.xml";
                service.LocalAddress = Configuration.GetSection("ServiceAddress").Value;
                service.DCAddress = "http://192.168.3.21:8500";
            });

            //app.UseHttpsRedirection();
            app.UseMvc();
        }
    }
}
