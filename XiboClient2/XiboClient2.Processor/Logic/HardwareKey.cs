﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading.Tasks;
using XiboClient2.Processor.Log;
using XiboClient2.Processor.Settings;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Security;

namespace XiboClient2.Processor.Logic
{
    class HardwareKey
    {
        private static object _locker = new object();

        private static AsymmetricCipherKeyPair _keys;
        private string _hardwareKey;
        private string _macAddress;
        private string _channel;

        public string Channel
        {
            get
            {
                if (String.IsNullOrEmpty(_channel))
                {
                    // Channel is based on the CMS URL, CMS Key and Hardware Key
                    _channel = Hashes.MD5(ApplicationSettings.Default.ServerUri + ApplicationSettings.Default.ServerKey + _hardwareKey);
                }

                return _channel;
            }
        }

        public void clearChannel()
        {
            _channel = null;
        }

        public string MacAddress
        {
            get
            {
                return _macAddress;
            }
        }

        public HardwareKey()
        {
            Debug.WriteLine("[IN]", "HardwareKey");

            // Get the Mac Address
            _macAddress = GetMacAddress();

            // Get the key from the Settings
            _hardwareKey = ApplicationSettings.Default.HardwareKey;

            // Is the key empty?
            if (_hardwareKey == "")
            {
                try
                {
                    string systemDriveLetter = Path.GetPathRoot(Environment.SystemDirectory);

                    // Calculate the Hardware key from the CPUID and Volume Serial
                    _hardwareKey = Hashes.MD5(GetCPUId() + GetVolumeSerial(systemDriveLetter[0].ToString()) + _macAddress);
                }
                catch
                {
                    _hardwareKey = "Change for Unique Key";
                }

                // Store the key
                ApplicationSettings.Default.HardwareKey = _hardwareKey;
            }

            Debug.WriteLine("[OUT]", "HardwareKey");
        }

        /// <summary>
        /// Gets the hardware key
        /// </summary>
        public string Key
        {
            get
            {
                return this._hardwareKey;
            }
        }

        /// <summary>
        /// Regenerates the hardware key
        /// </summary>
        public void Regenerate()
        {
            lock (_locker)
            {
                // Calculate the Hardware key from the CPUID and Volume Serial
                _hardwareKey = Hashes.MD5(GetCPUId() + GetVolumeSerial("C"));

                // Store the key
                ApplicationSettings.Default.HardwareKey = _hardwareKey;
                ApplicationSettings.Default.Save();
            }
        }

        /// <summary>
        /// return Volume Serial Number from hard drive
        /// </summary>
        /// <param name="strDriveLetter">[optional] Drive letter</param>
        /// <returns>[string] VolumeSerialNumber</returns>
        public string GetVolumeSerial(string strDriveLetter)
        {
            lock (_locker)
            {
                Debug.WriteLine("[IN]", "GetVolumeSerial");

                if (strDriveLetter == "" || strDriveLetter == null) strDriveLetter = "C";
                ManagementObject disk = new ManagementObject("win32_logicaldisk.deviceid=\"" + strDriveLetter + ":\"");
                disk.Get();

                System.Diagnostics.Debug.WriteLine("[OUT]", "GetVolumeSerial");

                return disk["VolumeSerialNumber"].ToString();
            }
        }

        /// <summary>
        /// Finds the MAC address of the first operation NIC found.
        /// </summary>
        /// <returns>The MAC address.</returns>
        private string GetMacAddress()
        {
            string macAddresses = string.Empty;

            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus == OperationalStatus.Up)
                    {
                        macAddresses += BitConverter.ToString(nic.GetPhysicalAddress().GetAddressBytes()).Replace('-', ':');
                        break;
                    }
                }
            }
            catch
            {
                macAddresses = "00:00:00:00:00:00";
            }

            return macAddresses;
        }

        /// <summary>
        /// Return processorId from first CPU in machine
        /// </summary>
        /// <returns>[string] ProcessorId</returns>
        public string GetCPUId()
        {
            lock (_locker)
            {
                Debug.WriteLine("[IN]", "GetCPUId");

                string cpuInfo = String.Empty;
                string temp = String.Empty;
                ManagementClass mc = new ManagementClass("Win32_Processor");
                ManagementObjectCollection moc = mc.GetInstances();
                foreach (ManagementObject mo in moc)
                {
                    if (cpuInfo == String.Empty)
                    {   // only return cpuInfo from first CPU
                        cpuInfo = mo.Properties["ProcessorId"].Value.ToString();
                    }
                }

                Debug.WriteLine("[OUT]", "GetCPUId");

                return cpuInfo;
            }
        }

        /// <summary>
        /// Get the XMR public key
        /// </summary>
        /// <returns></returns>
        public AsymmetricCipherKeyPair getXmrKey()
        {
            lock (_locker)
            {
                // Return the cached key if we have one.
                if (_keys != null)
                    return _keys;

                if (File.Exists(ApplicationSettings.Default.LibraryPath + "\\id_rsa"))
                {
                    try
                    {
                        using (TextReader textReader = new StringReader(File.ReadAllText(ApplicationSettings.Default.LibraryPath + "\\id_rsa")))
                        {
                            PemReader reader = new PemReader(textReader);
                            _keys = (AsymmetricCipherKeyPair)reader.ReadObject();
                        }

                        return _keys;
                    }
                    catch (Exception e)
                    {
                        File.Delete(ApplicationSettings.Default.LibraryPath + "\\id_rsa");

                        // Generate a new key
                        Trace.WriteLine(new LogMessage("HardwareKey - getXmrKey", "Unable to read existing key."), LogType.Info.ToString());
                        Trace.WriteLine(new LogMessage("HardwareKey - getXmrKey", "Unable to read existing key. e=" + e.Message), LogType.Audit.ToString());
                    }
                }

                // If we get here, we need to generate and save a key
                RsaKeyPairGenerator generator = new RsaKeyPairGenerator();
                generator.Init(new KeyGenerationParameters(new SecureRandom(), 1024));

                _keys = generator.GenerateKeyPair();

                // Save this key using PEM writer
                File.WriteAllText(ApplicationSettings.Default.LibraryPath + "\\id_rsa", getKeyAsString(_keys.Private));
                File.WriteAllText(ApplicationSettings.Default.LibraryPath + "\\id_rsa.pub", getKeyAsString(_keys.Public));

                return _keys;
            }
        }

        /// <summary>
        /// Get Public Key
        /// </summary>
        /// <returns></returns>
        public string getXmrPublicKey()
        {
            try
            {
                AsymmetricCipherKeyPair key = getXmrKey();

                return getKeyAsString(key.Public);
            }
            catch (Exception e)
            {
                Trace.WriteLine(new LogMessage("HardwareKey - getXmrPublicKey", "Unable to get XMR public key. E = " + e.Message), LogType.Error.ToString());
                return null;
            }
        }

        /// <summary>
        /// Get Key as string
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        private string getKeyAsString(AsymmetricKeyParameter key)
        {
            using (TextWriter textWriter = new StringWriter())
            {
                PemWriter writer = new PemWriter(textWriter);
                writer.WriteObject(key);
                writer.Writer.Flush();

                return textWriter.ToString();
            }
        }
    }
}
