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
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Timers;
using System.Xml;
using log4net;
using PacketDotNet;
using PacketDotNet.LLDP;
using SharpPcap;

namespace CiscoPhoneWMIProvider
{
    internal class PacketCapture
    {
        private List<Phone> phones;
        private IWebProxy proxy;
        private static readonly ILog log = LogManager.GetLogger(typeof(PacketCapture));

        public PacketCapture(string proxy)
        {
            phones = new List<Phone>();
            if (proxy != null)
            {
                log.Info(string.Format("Using web proxy: {0}", proxy));
                this.proxy = new WebProxy(proxy);
            }
        }

        public IEnumerable<Phone> TryCapture(int timeout)
        {
            var devs = CaptureDeviceList.Instance;

            foreach (var dev in devs)
            {
                try
                {
                    log.Info(string.Format("Opening device {0} for capturing in promiscuous mode.", dev.Description));
                    // Open device for capturing, ignoring non-ethernet devices.
                    dev.Open(DeviceMode.Promiscuous, timeout * 1000);

                    if (dev.LinkType != LinkLayers.Ethernet ||
                        dev.Description.Contains("VMware"))
                    {
                        log.Info(string.Format("Ignoring device {0} as it is not an ethernet device.", dev.Description));
                        dev.Close();
                        continue;
                    }
                }
                catch (Exception)
                {
                    log.Error(string.Format("Could not open device {0} for capturing.", dev.Description));
                    continue;
                }

                // Filter by LLDP protocol
                dev.Filter = "ether proto 0x88cc";

                // Get a single packet and parse into a Phone object
                log.Info("Capturing a single packet.");
                var raw = dev.GetNextPacket();
                dev.Close();

                if (raw != null)
                {
                    log.Info("Received LLDP packet, processing.");
                    var packet = EthernetPacket.ParsePacket(LinkLayers.Ethernet, raw.Data);
                    var lldpPacket = LLDPPacket.GetEncapsulated(packet);

                    Phone phone = ProcessPacket(lldpPacket);
                    if (phone != null)
                        yield return phone;
                }
                else
                {
                    log.Info("Could not capture a single packet before reaching the specified timeout. " +
                        "Causes: The timeout is too short, there is no Cisco IP Phone connected to the " +
                        "ethernet device, a firewall is blocking incoming LLDP packets or the IP Phone does not " +
                        "support LLDP protocol.");
                }
            }
        }

        private Phone ProcessPacket(LLDPPacket packet)
        {
            if (packet == null)
            {
                throw new ArgumentException("packet argument cannot be null");
            }

            var tlvs = packet.TlvCollection;
            Phone phone = null;

            log.Info("Processing LLDP packet TLVs");
            foreach (var tlv in tlvs)
            {
                if (tlv.Type == PacketDotNet.LLDP.TLVTypes.SystemCapabilities)
                {
                    // Not a telephone, ignore packet.
                    var cap = (SystemCapabilities)tlv;
                    if (!cap.IsEnabled(CapabilityOptions.Telephone))
                    {
                        log.Info("Packet was not sent by an IP phone, ignoring.");
                        return null;
                    }
                    else
                    {
                        log.Info("Packet has Telephone bit set.");
                    }
                }

                if (tlv.Type == PacketDotNet.LLDP.TLVTypes.ChassisID)
                {
                    // Subtype = Network address & IPv4.
                    // Needed to extract the IP address for querying the web service.
                    var cid = (ChassisID)tlv;
                    if (cid.SubType.HasFlag(ChassisSubTypes.NetworkAddress))
                    {
                        var ip = cid.NetworkAddress.Address;

                        // Query Cisco Phone web service
                        Uri uri = new Uri(
                            string.Format("http://{0}/CGI/Java/Serviceability?adapterX=device.statistics.device", ip.ToString()));
                        try
                        {
                            log.Info("Querying Cisco IP Phone web service.");
                            phone = QueryWebService(ip, uri, proxy);
                        }
                        catch (WebException)
                        {
                            // Could not query the web service.
                            log.Error("Could not query Cisco IP Phone web service.");
                            return null;
                        }
                    }
                    else
                    {
                        log.Warn("ChassisID TLV has no IPv4 subtype, ignoring packet.");
                    }
                }
            }
            return phone;
        }

        private Phone QueryWebService(IPAddress ip, Uri uri, IWebProxy proxy)
        {
            if (ip == null || uri == null)
            {
                throw new ArgumentException("arguments ip and uri cannot be null");
            }

            log.Info("Creating Web Request");
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(uri);
            if (proxy != null)
            {
                req.Proxy = proxy;
            }

            using (HttpWebResponse HttpWResp = (HttpWebResponse)req.GetResponse())
            {
                XmlDocument xmlDoc = new XmlDocument();
                log.Info("Loading XML document from web service.");
                xmlDoc.Load(HttpWResp.GetResponseStream());
                XmlNode root = xmlDoc.DocumentElement;

                var phone = new Phone();
                phone.Ip = ip;
                var mac = root.SelectSingleNode("MACAddress").InnerText;
                phone.MacAddress = PhysicalAddress.Parse(mac);
                var number = root.SelectSingleNode("phoneDN").InnerText;
                phone.Number = number;
                var serial = root.SelectSingleNode("serialNumber").InnerText;
                phone.SerialNumber = serial;
                var model = root.SelectSingleNode("modelNumber").InnerText;
                phone.Model = model;
                return phone;
            }
        }
    }
}