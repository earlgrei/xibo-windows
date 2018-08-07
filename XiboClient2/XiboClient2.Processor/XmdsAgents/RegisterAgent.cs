﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using XiboClient2.Processor.Log;
using XiboClient2.Processor.Logic;
using XiboClient2.Processor.Settings;

namespace XiboClient2.Processor.XmdsAgents
{
    class RegisterAgent
    {
        public static object _locker = new object();

        // Members to stop the thread
        private bool _forceStop = false;
        private ManualResetEvent _manualReset = new ManualResetEvent(false);

        // Events
        public delegate void OnXmrReconfigureDelegate();
        public event OnXmrReconfigureDelegate OnXmrReconfigure;

        /// <summary>
        /// Wake Up
        /// </summary>
        public void WakeUp()
        {
            _manualReset.Set();
        }

        /// <summary>
        /// Stops the thread
        /// </summary>
        public void Stop()
        {
            _forceStop = true;
            _manualReset.Set();
        }

        /// <summary>
        /// Runs the agent
        /// </summary>
        public void Run()
        {
            Trace.WriteLine(new LogMessage("RegisterAgent - Run", "Thread Started"), LogType.Info.ToString());

            while (!_forceStop)
            {
                lock (_locker)
                {
                    try
                    {
                        // If we are restarting, reset
                        _manualReset.Reset();

                        HardwareKey key = new HardwareKey();

                        Trace.WriteLine(new LogMessage("RegisterAgent - Run", "Thread Woken and Lock Obtained"), LogType.Info.ToString());

                        using (xmds.xmds xmds = new xmds.xmds())
                        {
                            xmds.Credentials = null;
                            xmds.Url = ApplicationSettings.Default.XiboClient_xmds_xmds + "&method=registerDisplay";
                            xmds.UseDefaultCredentials = false;

                            // Store the XMR address
                            string xmrAddress = ApplicationSettings.Default.XmrNetworkAddress;

                            RegisterAgent.ProcessRegisterXml(xmds.RegisterDisplay(
                                ApplicationSettings.Default.ServerKey,
                                key.Key,
                                ApplicationSettings.Default.DisplayName,
                                "windows",
                                ApplicationSettings.Default.ClientVersion,
                                ApplicationSettings.Default.ClientCodeVersion,
                                Environment.OSVersion.ToString(),
                                key.MacAddress,
                                key.Channel,
                                key.getXmrPublicKey()));

                            // Set the flag to indicate we have a connection to XMDS
                            ApplicationSettings.Default.XmdsLastConnection = DateTime.Now;

                            // Has the XMR address changed?
                            if (xmrAddress != ApplicationSettings.Default.XmrNetworkAddress)
                            {
                                OnXmrReconfigure();
                            }

                            // Is the timezone empty?
                            if (string.IsNullOrEmpty(ApplicationSettings.Default.DisplayTimeZone))
                            {
                                reportTimezone();
                            }
                        }
                    }
                    catch (WebException webEx)
                    {
                        // Increment the quantity of XMDS failures and bail out
                        ApplicationSettings.Default.IncrementXmdsErrorCount();

                        // Log this message, but dont abort the thread
                        Trace.WriteLine(new LogMessage("RegisterAgent - Run", "WebException in Run: " + webEx.Message), LogType.Info.ToString());
                    }
                    catch (Exception ex)
                    {
                        // Log this message, but dont abort the thread
                        Trace.WriteLine(new LogMessage("RegisterAgent - Run", "Exception in Run: " + ex.Message), LogType.Info.ToString());
                    }
                }

                // Sleep this thread until the next collection interval
                _manualReset.WaitOne((int)(ApplicationSettings.Default.CollectInterval * ApplicationSettings.Default.XmdsCollectionIntervalFactor() * 1000));
            }

            Trace.WriteLine(new LogMessage("RegisterAgent - Run", "Thread Stopped"), LogType.Info.ToString());
        }

        public static string ProcessRegisterXml(string xml)
        {
            string message = "";

            try
            {
                // Load the result into an XML document
                XmlDocument result = new XmlDocument();
                result.LoadXml(xml);

                // Test the XML
                if (result.DocumentElement.Attributes["code"].Value == "READY")
                {
                    // Get the config element
                    if (result.DocumentElement.ChildNodes.Count <= 0)
                        throw new Exception("Configuration not set for this display");

                    // Hash after removing the date
                    try
                    {
                        if (result.DocumentElement.Attributes["date"] != null)
                            result.DocumentElement.Attributes["date"].Value = "";

                        if (result.DocumentElement.Attributes["localDate"] != null)
                            result.DocumentElement.Attributes["localDate"].Value = "";
                    }
                    catch
                    {
                        // No date, no need to remove
                    }

                    string md5 = Hashes.MD5(result.OuterXml);

                    if (md5 == ApplicationSettings.Default.Hash)
                        return result.DocumentElement.Attributes["message"].Value;

                    // Populate the settings based on the XML we've received.
                    ApplicationSettings.Default.PopulateFromXml(result);

                    // Store the MD5 hash and the save
                    ApplicationSettings.Default.Hash = md5;
                    ApplicationSettings.Default.Save();
                }
                else
                {
                    message += result.DocumentElement.Attributes["message"].Value;
                }

                // Append the informational message with the message attribute.
                message = result.DocumentElement.Attributes["message"].Value + Environment.NewLine + message;
            }
            catch (Exception ex)
            {
                message += ex.Message;
            }

            return message;
        }

        /// <summary>
        /// Report the timezone to XMDS
        /// </summary>
        private void reportTimezone()
        {
            using (xmds.xmds xmds = new xmds.xmds())
            {
                string status = "{\"timeZone\":\"" + WindowsToIana(TimeZone.CurrentTimeZone.StandardName) + "\"}";

                xmds.Credentials = null;
                xmds.Url = ApplicationSettings.Default.XiboClient_xmds_xmds + "&method=notifyStatus";
                xmds.UseDefaultCredentials = false;
                xmds.NotifyStatusAsync(ApplicationSettings.Default.ServerKey, ApplicationSettings.Default.HardwareKey, status);
            }
        }

        /// <summary>
        /// Windows to IANA timezone mapping
        /// ref: http://stackoverflow.com/questions/17348807/how-to-translate-between-windows-and-iana-time-zones
        /// </summary>
        /// <param name="windowsZoneId"></param>
        /// <returns></returns>
        private string WindowsToIana(string windowsZoneId)
        {
            string result = "Etc/UTC";
            if (windowsZoneId.Equals("UTC", StringComparison.Ordinal))
                return "Etc/UTC";

            //var source = NodaTime.TimeZones.TzdbDateTimeZoneSource.Default;
            //string result;
            //// If there's no such mapping, result will be null.
            //source.WindowsMapping.PrimaryMapping.TryGetValue(windowsZoneId, out result);
            //// Canonicalize
            //if (result != null)
            //{
            //    result = source.CanonicalIdMap[result];
            //}
            return result;
        }
    }
}
