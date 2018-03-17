using CDSProxy.Helper;
using Microsoft.Azure.Devices.Client;
using Newtonsoft.Json.Linq;
using Opc.Ua;
using System;
using System.Text;
using System.Threading;

namespace CDSProxy
{
    class Program
    {   
        static void Main(string[] args)
        {
            try
            { 
                while (true)
                {
                    // ProcessMessageCommand
                    MessageCommand msgCommand = new MessageCommand();
                    msgCommand.Start().Wait();
                    msgCommand = null;

                    Console.WriteLine("Sleep 10 seconds");
                    Thread.Sleep(10000);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Exit due to Exception: {0}", ex.Message);
            }
        }
    }
}
