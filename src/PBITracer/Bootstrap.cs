using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.IO;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;

namespace PBITracer
{
    public static class Bootstrap
    {
        public static IServiceProvider ConfigureServices()
        {
            // Add Config

            var appPath = Directory.GetCurrentDirectory();

            var appSettings = new Dictionary<string, string>
                {                
                    {"AppPath", appPath}
                };

            var appSettingsFile = Path.Combine(appPath, "appsettings.json");

            var configuration = new ConfigurationBuilder()   
                 .SetBasePath(appPath)
                 .AddInMemoryCollection(appSettings)                                  
                 .AddJsonFile(appSettingsFile, optional: true, reloadOnChange: true)
                 .Build();

            var configFileExists = false;

            if (File.Exists(appSettingsFile))
            {
                configFileExists = true;
            }

            var serviceCollection = new ServiceCollection();

            // Add logging

            serviceCollection.AddLogging(loggingBuilder =>
            {                
                var logging = loggingBuilder.AddConfiguration(configuration.GetSection("Logging"));
                        
                if (!configFileExists)
                {                    
                    loggingBuilder.AddSimpleConsole(options =>
                    {
                        options.SingleLine = true;                            
                    }).SetMinimumLevel(LogLevel.Debug);                    
                }
                else
                {
                    loggingBuilder.AddConsole();
                }
                
                loggingBuilder.AddDebug();                
            });
            
            // Add Services

            serviceCollection.AddSingleton(configuration);

            var serviceProvider = serviceCollection.BuildServiceProvider();

            return serviceProvider;
        }
    }
}
