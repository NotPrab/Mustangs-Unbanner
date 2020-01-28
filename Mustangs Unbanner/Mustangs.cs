using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Net;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Net.NetworkInformation;
using System.Management;

namespace Mustangs_Unbanner
{
    public partial class Mustangs : Form
    {
        public Mustangs()
        {
            InitializeComponent();
        }

        public class Adapter
        {
            public ManagementObject adapter;
            public string adaptername;
            public string customname;
            public int devnum;

            public Adapter(ManagementObject a, string aname, string cname, int n)
            {
                this.adapter = a;
                this.adaptername = aname;
                this.customname = cname;
                this.devnum = n;
            }

            public Adapter(NetworkInterface i) : this(i.Description) { }

            public Adapter(string aname)
            {
                this.adaptername = aname;

                var searcher = new ManagementObjectSearcher("select * from win32_networkadapter where Name='" + adaptername + "'");
                var found = searcher.Get();
                this.adapter = found.Cast<ManagementObject>().FirstOrDefault();

                // Extract adapter number; this should correspond to the keys under
                // HKEY_LOCAL_MACHINE\SYSTEM\ControlSet001\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}
                try
                {
                    var match = Regex.Match(adapter.Path.RelativePath, "\\\"(\\d+)\\\"$");
                    this.devnum = int.Parse(match.Groups[1].Value);
                }
                catch
                {
                    return;
                }

                // Find the name the user gave to it in "Network Adapters"
                this.customname = NetworkInterface.GetAllNetworkInterfaces().Where(
                    i => i.Description == adaptername
                ).Select(
                    i => " (" + i.Name + ")"
                ).FirstOrDefault();
            }

            /// <summary>
            /// Get the .NET managed adapter.
            /// </summary>
            public NetworkInterface ManagedAdapter
            {
                get
                {
                    return NetworkInterface.GetAllNetworkInterfaces().Where(
                        nic => nic.Description == this.adaptername
                    ).FirstOrDefault();
                }
            }

            /// <summary>
            /// Get the MAC address as reported by the adapter.
            /// </summary>
            public string Mac
            {
                get
                {
                    try
                    {
                        return BitConverter.ToString(this.ManagedAdapter.GetPhysicalAddress().GetAddressBytes()).Replace("-", "").ToUpper();
                    }
                    catch { return null; }
                }
            }

            /// <summary>
            /// Get the registry key associated to this adapter.
            /// </summary>
            public string RegistryKey
            {
                get
                {
                    return String.Format(@"SYSTEM\ControlSet001\Control\Class\{{4D36E972-E325-11CE-BFC1-08002BE10318}}\{0:D4}", this.devnum);
                }
            }

            /// <summary>
            /// Get the NetworkAddress registry value of this adapter.
            /// </summary>
            public string RegistryMac
            {
                get
                {
                    try
                    {
                        using (RegistryKey regkey = Registry.LocalMachine.OpenSubKey(this.RegistryKey, false))
                        {
                            return regkey.GetValue("NetworkAddress").ToString();
                        }
                    }
                    catch
                    {
                        return null;
                    }
                }
            }

            /// <summary>
            /// Sets the NetworkAddress registry value of this adapter.
            /// </summary>
            /// <param name="value">The value. Should be EITHER a string of 12 hexadecimal digits, uppercase, without dashes, dots or anything else, OR an empty string (clears the registry value).</param>
            /// <returns>true if successful, false otherwise</returns>
            public bool SetRegistryMac(string value)
            {
                bool shouldReenable = false;

                try
                {
                    // If the value is not the empty string, we want to set NetworkAddress to it,
                    // so it had better be valid
                    if (value.Length > 0 && !Adapter.IsValidMac(value, false))
                        throw new Exception(value + " is not a valid mac address");

                    using (RegistryKey regkey = Registry.LocalMachine.OpenSubKey(this.RegistryKey, true))
                    {
                        if (regkey == null)
                            throw new Exception("Failed to open the registry key");

                        // Sanity check
                        if (regkey.GetValue("AdapterModel") as string != this.adaptername
                            && regkey.GetValue("DriverDesc") as string != this.adaptername)
                            throw new Exception("Adapter not found in registry");

                        // Ask if we really want to do this
                        string question = value.Length > 0 ?
                            "Changing MAC-adress of adapter {0} from {1} to {2}. Proceed?" :
                            "Clearing custom MAC-address of adapter {0}. Proceed?";
                        DialogResult proceed = MessageBox.Show(
                            String.Format(question, this.ToString(), this.Mac, value),
                            "Change MAC-address?", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                        if (proceed != DialogResult.Yes)
                            return false;

                        // Attempt to disable the adepter
                        var result = (uint)adapter.InvokeMethod("Disable", null);
                        if (result != 0)
                            throw new Exception("Failed to disable network adapter.");

                        // If we're here the adapter has been disabled, so we set the flag that will re-enable it in the finally block
                        shouldReenable = true;

                        // If we're here everything is OK; update or clear the registry value
                        if (value.Length > 0)
                            regkey.SetValue("NetworkAddress", value, RegistryValueKind.String);
                        else
                            regkey.DeleteValue("NetworkAddress");


                        return true;
                    }
                }

                catch (Exception ex)
                {
                    MessageBox.Show(ex.ToString());
                    return false;
                }

                finally
                {
                    if (shouldReenable)
                    {
                        uint result = (uint)adapter.InvokeMethod("Enable", null);
                        if (result != 0)
                            MessageBox.Show("Failed to re-enable network adapter.");
                    }
                }
            }
            public override string ToString()
            {
                return this.adaptername + this.customname;
            }

            /// <summary>
            /// Get a random (locally administered) MAC address.
            /// </summary>
            /// <returns>A MAC address having 01 as the least significant bits of the first byte, but otherwise random.</returns>
            public static string GetNewMac()
            {
                System.Random r = new System.Random();

                byte[] bytes = new byte[6];
                r.NextBytes(bytes);

                // Set second bit to 1
                bytes[0] = (byte)(bytes[0] | 0x02);
                // Set first bit to 0
                bytes[0] = (byte)(bytes[0] & 0xfe);

                return MacToString(bytes);
            }

            /// <summary>
            /// Verifies that a given string is a valid MAC address.
            /// </summary>
            /// <param name="mac">The string.</param>
            /// <param name="actual">false if the address is a locally administered address, true otherwise.</param>
            /// <returns>true if the string is a valid MAC address, false otherwise.</returns>
            public static bool IsValidMac(string mac, bool actual)
            {
                // 6 bytes == 12 hex characters (without dashes/dots/anything else)
                if (mac.Length != 12)
                    return false;

                // Should be uppercase
                if (mac != mac.ToUpper())
                    return false;

                // Should not contain anything other than hexadecimal digits
                if (!Regex.IsMatch(mac, "^[0-9A-F]*$"))
                    return false;

                if (actual)
                    return true;

                // If we're here, then the second character should be a 2, 6, A or E
                char c = mac[1];
                return (c == '2' || c == '6' || c == 'A' || c == 'E');
            }

            /// <summary>
            /// Verifies that a given MAC address is valid.
            /// </summary>
            /// <param name="mac">The address.</param>
            /// <param name="actual">false if the address is a locally administered address, true otherwise.</param>
            /// <returns>true if valid, false otherwise.</returns>
            public static bool IsValidMac(byte[] bytes, bool actual)
            {
                return IsValidMac(Adapter.MacToString(bytes), actual);
            }

            /// <summary>
            /// Converts a byte array of length 6 to a MAC address (i.e. string of hexadecimal digits).
            /// </summary>
            /// <param name="bytes">The bytes to convert.</param>
            /// <returns>The MAC address.</returns>
            public static string MacToString(byte[] bytes)
            {
                return BitConverter.ToString(bytes).Replace("-", "").ToUpper();
            }
        }

        private void gunaLinePanel1_Paint(object sender, PaintEventArgs e)
        {
            // ignore this
        }

        private void Mustangs_Load(object sender, EventArgs e)
        {
            /* Windows generally seems to add a number of non-physical devices, of which
             * we would not want to change the address. Most of them have an impossible
             * MAC address. */
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces().Where(
                    a => Adapter.IsValidMac(a.GetPhysicalAddress().GetAddressBytes(), true)
                ).OrderByDescending(a => a.Speed))
            {
                AdaptersComboBox.Items.Add(new Adapter(adapter));
            }

            AdaptersComboBox.SelectedIndex = 0;
            timer1.Start();
            foreach (string subkeyname2 in Registry.CurrentUser.GetSubKeyNames())
            {
                if (subkeyname2.StartsWith("1") || subkeyname2.StartsWith("2") || subkeyname2.StartsWith("3") || subkeyname2.StartsWith("4") || subkeyname2.StartsWith("5") || subkeyname2.StartsWith("6") || subkeyname2.StartsWith("7") || subkeyname2.StartsWith("8") || subkeyname2.StartsWith("9"))
                {
                    longkey.Text = subkeyname2;
                    rtb.Text = "->The long key " + longkey.Text + " is found!";
                    break;
                }
            }
            foreach (string subkeyname in Registry.CurrentUser.OpenSubKey("Software").OpenSubKey("Microsoft").GetSubKeyNames())
            {
                if (subkeyname.StartsWith("1") || subkeyname.StartsWith("2") || subkeyname.StartsWith("3") || subkeyname.StartsWith("4") || subkeyname.StartsWith("5") || subkeyname.StartsWith("6") || subkeyname.StartsWith("7") || subkeyname.StartsWith("8") || subkeyname.StartsWith("9"))
                {
                    shortkey.Text = subkeyname;
                    rtb.Text = rtb.Text + Environment.NewLine;
                    rtb.Text = rtb.Text + "->The short key " + shortkey.Text + " is found!";
                    break;
                }
            }
            if (longkey.Text == "no longkey(re-logon gt to find it)")
            {
                rtb.Text = "->The long key isn't found!";
            }
            if (shortkey.Text == "no shortkey(re-logon gt to find it)")
            {
                rtb.Text = rtb.Text + Environment.NewLine;
                rtb.Text = rtb.Text + "->The short key isn't found";
            }
            rtb.Text = rtb.Text + Environment.NewLine;
            rtb.Text = rtb.Text + "->MachineGuid key is found!";
        }
        private void UpdateAddresses()
        {
            Adapter a = AdaptersComboBox.SelectedItem as Adapter;
            this.CurrentMacTextBox.Text = a.RegistryMac;
            this.ActualMacLabel.Text = a.Mac;
        }

        private void timer1_Tick(object sender, EventArgs e)
        {
            foreach (string subkeyname in Registry.CurrentUser.OpenSubKey("Software").OpenSubKey("Microsoft").GetSubKeyNames())
            {
                if (subkeyname.StartsWith("1") || subkeyname.StartsWith("2") || subkeyname.StartsWith("3") || subkeyname.StartsWith("4") || subkeyname.StartsWith("5") || subkeyname.StartsWith("6") || subkeyname.StartsWith("7") || subkeyname.StartsWith("8") || subkeyname.StartsWith("9"))
                {
                    shortkey.Text = subkeyname;
                    break;
                }
            }
            foreach (string subkeyname2 in Registry.CurrentUser.GetSubKeyNames())
            {
                if (subkeyname2.StartsWith("1") || subkeyname2.StartsWith("2") || subkeyname2.StartsWith("3") || subkeyname2.StartsWith("4") || subkeyname2.StartsWith("5") || subkeyname2.StartsWith("6") || subkeyname2.StartsWith("7") || subkeyname2.StartsWith("8") || subkeyname2.StartsWith("9"))
                {
                    longkey.Text = subkeyname2;
                    break;
                }
            }
        }

        private void button1_Click(object sender, EventArgs e)
        {
            if (longkey.Text != "no longkey(re-logon gt to find it)" && shortkey.Text != "no shortkey(re-logon gt to find it)")
            {
                Registry.CurrentUser.DeleteSubKey(longkey.Text);
                rtb.Text = rtb.Text + Environment.NewLine;
                rtb.Text = rtb.Text + "->The long key " + longkey.Text + " is deleted!";
                string microsoftKey = @"Software\Microsoft\" + shortkey.Text;
                Registry.CurrentUser.DeleteSubKey(microsoftKey);
                rtb.Text = rtb.Text + Environment.NewLine;
                rtb.Text = rtb.Text + "->The short key " + shortkey.Text + " is deleted!";
                string cryptographyKey = @"SOFTWARE\Microsoft\Cryptography";
                RegistryKey ckey = Registry.LocalMachine.OpenSubKey(cryptographyKey, true);
                ckey.DeleteValue("MachineGuid");
                rtb.Text = rtb.Text + Environment.NewLine;
                rtb.Text = rtb.Text + "->The MachineGuid key is deleted!";
                longkey.Text = "no longkey(re-logon gt to find it)";
                shortkey.Text = "no shortkey(re-logon gt to find it)";
                rtb.Text = rtb.Text + Environment.NewLine;
                rtb.Text = rtb.Text + "->Done!";
                MessageBox.Show("Done unbanning! Now you need to make adapter and use vpn! If you will relog on growtopia before making adapter and vpn it won't work!");
            }
            else
            {
                rtb.Text = rtb.Text + Environment.NewLine;
                rtb.Text = rtb.Text + "->Can't unban because no long and short keys! Go re-log on Growtopia to make it work!";
                MessageBox.Show("Can't unban because no long and short keys! Go re-log on Growtopia to make it work!");
            }
        }

        private void RereadButton_Click(object sender, EventArgs e)
        {
            UpdateAddresses();
        }

        private void UpdateButton_Click(object sender, EventArgs e)
        {
            if (!Adapter.IsValidMac(CurrentMacTextBox.Text, false))
            {
                MessageBox.Show("Entered MAC-address is not valid; will not update.", "Invalid MAC-address specified", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            SetRegistryMac(CurrentMacTextBox.Text);
        }

        private void RandomButton_Click(object sender, EventArgs e)
        {
            CurrentMacTextBox.Text = Adapter.GetNewMac();
        }

        private void ClearButton_Click(object sender, EventArgs e)
        {
            SetRegistryMac("");
        }

        private void SetRegistryMac(string mac)
        {
            Adapter a = AdaptersComboBox.SelectedItem as Adapter;

            if (a.SetRegistryMac(mac))
            {
                System.Threading.Thread.Sleep(100);
                UpdateAddresses();
                MessageBox.Show("Done!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void AdaptersComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            UpdateAddresses();
        }


    }
}

