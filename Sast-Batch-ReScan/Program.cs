using Checkmarx.API;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace BatchReScanPerLanguageAndDateWithQueue
{
    class Program
    {
        public static IConfigurationRoot configuration;

        static void Main(string[] args)
        {
            ServiceCollection serviceCollection = new ServiceCollection();
            
            ConfigureServices(serviceCollection);

            var sastConcurrentScans = int.Parse(configuration["SAST_ConcurrentScans"]);
            var sastUsername        = configuration["SAST_Username"];
            var sastPassword        = configuration["SAST_Password"];
            var sastServerUrl       = new Uri(configuration["SAST_ServerUrl"]);

            var client              = new CxClient(sastServerUrl, sastUsername, sastPassword);
            var dic                 = client.GetScansForLanguageNotScannedSince("Java", true, DateTime.Parse("2021-12-15"));

            while(dic.Count() > 0)
            {
                Console.WriteLine($"Dic count: {dic.Count()} at {DateTime.Now}");

                var p = dic.First();

                var queuedScans = client.GetScanQueue()
                    .Where(x => x.stage.value != "Finished")
                    .Where(x => x.stage.value != "Failed")
                    .ToList();

                var alreadyQueued = queuedScans.Where(x => x.project.id == p.ProjectId).Any();

                if(alreadyQueued)
                {
                    dic = dic.Skip(1);
                }
                else
                {
                    if (queuedScans.Count <= sastConcurrentScans)
                    {
                        try
                        {
                            client.RunSASTScan(p.ProjectId);
                        }
                        catch (NotSupportedException e)
                        {
                            Console.WriteLine(e.Message);
                            Console.WriteLine("Scanned via zip upload, will try to download and reupload source");

                            var source = client.GetSourceCode(p.ScanId);

                            client.ReScan(p.ProjectId, source, "Log4Shell automatic recalculation");
                        }

                        Console.WriteLine("queued scan for projectId: " + p.ProjectId + ", from scanId : " + p.ScanId);

                        dic = dic.Skip(1);
                    }
                    else
                    {
                        Console.WriteLine("we have " + queuedScans.Count + " scans in the queue");
                        Console.WriteLine("sleeping for 60 secs...");
                        Thread.Sleep(60 * 1000);
                    }
                }
            }

            Console.ReadKey();
        }

        private static void ConfigureServices(ServiceCollection serviceCollection)
        {
            // Build configuration
            configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetParent(AppContext.BaseDirectory).FullName)
                .AddJsonFile("appsettings.json", false)
                .Build();
        }
    }
}
