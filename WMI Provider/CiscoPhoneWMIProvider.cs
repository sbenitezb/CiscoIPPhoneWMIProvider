// Copyright (C) 2012 Sebastián Benítez <sbenitezb@gmail.com>
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to
// deal in the Software without restriction, including without limitation the
// rights to use, copy, modify, merge, publish, distribute, sublicense, and/or
// sell copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Management.Instrumentation;
using System.ServiceProcess;
using System.Threading;
using log4net;
using log4net.Appender;
using log4net.Config;
using Microsoft.Win32;

// Specify the namespace into which the data
// should be published
[assembly: WmiConfiguration(@"root\MTE", HostingModel = ManagementHostingModel.LocalSystem)]
[assembly: CLSCompliant(true)]

namespace CiscoPhoneWMIProvider
{
    // Let the system know that the InstallUtil.exe
    // tool will be run against this assembly
    [System.ComponentModel.RunInstaller(true)]
    public class MyInstaller : DefaultManagementInstaller
    {
        public override void Install(IDictionary stateSaver)
        {
            base.Install(stateSaver);
            var rs = new System.Runtime.InteropServices.RegistrationServices();
        }

        public override void Uninstall(IDictionary savedState)
        {
            try
            {
                var mc = new ManagementClass(@"root\MTE:CiscoIPPhone");
                mc.Delete();
            }
            catch { }

            try
            {
                base.Uninstall(savedState);
            }
            catch { }
        }
    }

    [ManagementEntity(Name = "CiscoIPPhone")]
    public class CiscoIpPhone
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(CiscoIpPhone));

        [ManagementKey]
        public string MacAddress { get; set; }

        [ManagementProbe]
        public string IPAddress { get; set; }

        [ManagementProbe]
        public string PhoneNumber { get; set; }

        [ManagementProbe]
        public string Model { get; set; }

        [ManagementProbe]
        public string SerialNumber { get; set; }

        public CiscoIpPhone(string mac, string ipAddress, string phoneNumber, string model, string serialNumber)
        {
            MacAddress = mac;
            IPAddress = ipAddress;
            PhoneNumber = phoneNumber;
            Model = model;
            SerialNumber = serialNumber;
        }

        [ManagementEnumerator]
        static public IEnumerable GetCiscoIpPhone()
        {
            var timeout = 33;
            string proxy = null;

            // Configure logger
            var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var logpath = string.Format(@"{0}\{1}", windir, "CiscoPhoneWMIProvider.log4net");
            var logConf = new FileInfo(logpath);
            log4net.Config.XmlConfigurator.Configure(logConf);

            log.Info("GetCiscoIpPhone enumerator called by WMI host process.");
            try
            {
                // Get some configuration defaults
                log.Debug("Fetching configuration from registry key.");
                using (var regKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\MTE\CiscoPhoneWMIProvider"))
                {
                    timeout = (int)regKey.GetValue("Timeout", 33);
                    proxy = (string)regKey.GetValue("Proxy");
                }
            }
            catch (Exception)
            {
                log.Warn("Could not open registry key SOFTWARE\\MTE\\CiscoPhoneWMIProvider " +
                    "to get configuration. Using defaults.");
            }

            // Ensure WinPCap service is running
            ServiceController sc = null;
            try
            {
                log.Info("Checking if WinPCap service is running.");
                sc = new ServiceController("npf");
                for (var i = 0; i < 3 && sc.Status != ServiceControllerStatus.Running; i++)
                {
                    log.Info("Sending Start command to npf service.");
                    if (sc.Status == ServiceControllerStatus.Stopped)
                    {
                        sc.Start();
                        Thread.Sleep(1000);
                        sc.Refresh();
                    }
                }
            }
            catch (ArgumentException)
            {
                log.Fatal("WinPCap driver is not available!");
                yield break;
            }

            if (sc.Status != ServiceControllerStatus.Running)
            {
                // Could not start service
                log.Error("Could not start WinPCap service.");
                yield break;
            }

            // Start capturing LLDP packets to discover Cisco IP Phones
            var capturer = new PacketCapture(proxy);
            log.Info(string.Format("Starting capture process with a timeout of {0}ms", timeout * 1000));
            foreach (var phone in capturer.TryCapture(timeout))
            {
                log.Debug(phone.ToString());
                yield return new CiscoIpPhone(
                    phone.MacAddress.ToString(),
                    phone.Ip.ToString(),
                    phone.Number,
                    phone.Model,
                    phone.SerialNumber);
            }

            // Stop the WinPCap service after finished returning data.
            // This is necessary because WinPCap does not implement a security
            // model that restricts normal users from using the driver.
            log.Info("Stopping the WinPCap service.");
            if (sc.CanStop) sc.Stop();
        }
    }
}