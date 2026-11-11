using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PCManagerCS
{
    /// <summary>IP / DNS 설정 변경. 프로그램 재시작 없이 netsh만 관리자 권한으로 실행.</summary>
    internal sealed class IPConfigForm : Form
    {
        private readonly Action<string> log;
        private readonly ComboBox adapterCombo;
        private readonly Label currentLabel;
        private readonly RadioButton dhcpRadio, manualRadio;
        private readonly TextBox ipBox, maskBox, gwBox, dns1Box, dns2Box;
        private readonly Panel manualPanel;
        private readonly Button applyBtn;
        private List<NetworkInterface> adapters = new List<NetworkInterface>();

        public IPConfigForm(Action<string> log)
        {
            this.log = log;
            Text = "IP 설정 변경";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Theme.Surface;
            Font = Theme.Regular;
            ClientSize = new Size(Theme.S(440), Theme.S(470));

            int x = Theme.S(20), w = Theme.S(400), y = Theme.S(16);

            Controls.Add(new Label { Text = "네트워크 어댑터", Font = Theme.Bold, Location = new Point(x, y), AutoSize = true });
            y += Theme.S(24);
            adapterCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(x, y), Width = w };
            Controls.Add(adapterCombo);
            y += Theme.S(34);

            currentLabel = new Label
            {
                Location = new Point(x, y),
                Size = new Size(w, Theme.S(92)),
                BackColor = Theme.Bg,
                ForeColor = Theme.SubText,
                Padding = new Padding(Theme.S(10), Theme.S(8), Theme.S(8), Theme.S(8))
            };
            Controls.Add(currentLabel);
            y += Theme.S(104);

            dhcpRadio = new RadioButton { Text = "자동으로 IP 주소 받기 (DHCP)", Location = new Point(x, y), AutoSize = true, Checked = true };
            y += Theme.S(26);
            manualRadio = new RadioButton { Text = "다음 IP 주소 사용 (고정 IP)", Location = new Point(x, y), AutoSize = true };
            y += Theme.S(30);
            Controls.Add(dhcpRadio);
            Controls.Add(manualRadio);

            manualPanel = new Panel { Location = new Point(x, y), Size = new Size(w, Theme.S(170)) };
            int ry = 0;
            TextBox Row(string label)
            {
                manualPanel.Controls.Add(new Label { Text = label, Location = new Point(Theme.S(18), ry + Theme.S(3)), AutoSize = true });
                var tb = new TextBox { Location = new Point(Theme.S(140), ry), Width = Theme.S(200) };
                manualPanel.Controls.Add(tb);
                ry += Theme.S(32);
                return tb;
            }
            ipBox = Row("IP 주소");
            maskBox = Row("서브넷 마스크");
            gwBox = Row("기본 게이트웨이");
            dns1Box = Row("기본 DNS");
            dns2Box = Row("보조 DNS (선택)");
            Controls.Add(manualPanel);

            applyBtn = Theme.FlatButton("적용", true);
            var cancelBtn = Theme.FlatButton("닫기");
            cancelBtn.DialogResult = DialogResult.Cancel;
            Controls.Add(applyBtn);
            Controls.Add(cancelBtn);
            CancelButton = cancelBtn;
            Shown += (s, e) =>
            {
                int by = ClientSize.Height - Theme.S(16) - applyBtn.Height;
                cancelBtn.Location = new Point(ClientSize.Width - Theme.S(20) - cancelBtn.Width, by);
                applyBtn.Location = new Point(cancelBtn.Left - Theme.S(8) - applyBtn.Width, by);
            };

            dhcpRadio.CheckedChanged += (s, e) => manualPanel.Enabled = manualRadio.Checked;
            adapterCombo.SelectedIndexChanged += (s, e) => ShowCurrent();
            applyBtn.Click += async (s, e) => await Apply();
            manualPanel.Enabled = false;

            LoadAdapters();
        }

        private void LoadAdapters()
        {
            adapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                            n.Supports(NetworkInterfaceComponent.IPv4))
                .OrderBy(n => n.GetIPProperties().GatewayAddresses.Count == 0) // 게이트웨이 있는 어댑터 우선
                .ThenBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                .ToList();
            adapterCombo.Items.Clear();
            foreach (var n in adapters) adapterCombo.Items.Add($"{n.Name}  —  {n.Description}");
            if (adapters.Count > 0) adapterCombo.SelectedIndex = 0;
            else currentLabel.Text = "연결된 네트워크 어댑터가 없습니다.";
            applyBtn.Enabled = adapters.Count > 0;
        }

        private NetworkInterface Selected =>
            adapterCombo.SelectedIndex >= 0 && adapterCombo.SelectedIndex < adapters.Count ? adapters[adapterCombo.SelectedIndex] : null;

        private void ShowCurrent()
        {
            var n = Selected;
            if (n == null) return;
            var p = n.GetIPProperties();
            var a = p.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
            string gw = p.GatewayAddresses.Select(g => g.Address).FirstOrDefault(g => g.AddressFamily == AddressFamily.InterNetwork)?.ToString() ?? "";
            var dns = p.DnsAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetwork).Select(d => d.ToString()).ToList();
            bool dhcp = true;
            try { dhcp = p.GetIPv4Properties()?.IsDhcpEnabled ?? true; } catch { }

            currentLabel.Text =
                $"현재 설정  ({(dhcp ? "DHCP 자동" : "고정 IP")})\n" +
                $"IP 주소: {a?.Address}   서브넷: {a?.IPv4Mask}\n" +
                $"게이트웨이: {gw}\n" +
                $"DNS: {string.Join(", ", dns)}";

            dhcpRadio.Checked = dhcp;
            manualRadio.Checked = !dhcp;
            manualPanel.Enabled = !dhcp;
            ipBox.Text = a?.Address.ToString() ?? "";
            maskBox.Text = a?.IPv4Mask?.ToString() ?? "255.255.255.0";
            gwBox.Text = gw;
            dns1Box.Text = dns.ElementAtOrDefault(0) ?? "";
            dns2Box.Text = dns.ElementAtOrDefault(1) ?? "";
        }

        private static bool IsIPv4(string s) =>
            IPAddress.TryParse(s, out var a) && a.AddressFamily == AddressFamily.InterNetwork && s.Count(c => c == '.') == 3;

        private async Task Apply()
        {
            var n = Selected;
            if (n == null) return;
            string name = n.Name;
            string cmd;

            if (dhcpRadio.Checked)
            {
                cmd = $"netsh interface ipv4 set address name=\"{name}\" source=dhcp & " +
                      $"netsh interface ipv4 set dnsservers name=\"{name}\" source=dhcp";
            }
            else
            {
                string ip = ipBox.Text.Trim(), mask = maskBox.Text.Trim(), gw = gwBox.Text.Trim(),
                       d1 = dns1Box.Text.Trim(), d2 = dns2Box.Text.Trim();
                string err = !IsIPv4(ip) ? "IP 주소" : !IsIPv4(mask) ? "서브넷 마스크" :
                             gw.Length > 0 && !IsIPv4(gw) ? "기본 게이트웨이" :
                             d1.Length > 0 && !IsIPv4(d1) ? "기본 DNS" :
                             d2.Length > 0 && !IsIPv4(d2) ? "보조 DNS" : null;
                if (err != null)
                {
                    MessageBox.Show(this, $"{err} 형식이 올바르지 않습니다. (예: 192.168.0.10)", "입력 오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                cmd = $"netsh interface ipv4 set address name=\"{name}\" static {ip} {mask}{(gw.Length > 0 ? " " + gw : "")}";
                if (d1.Length > 0) cmd += $" && netsh interface ipv4 set dnsservers name=\"{name}\" static {d1} primary validate=no";
                else cmd += $" && netsh interface ipv4 set dnsservers name=\"{name}\" source=dhcp";
                if (d2.Length > 0) cmd += $" && netsh interface ipv4 add dnsservers name=\"{name}\" {d2} index=2 validate=no";
            }

            applyBtn.Enabled = false;
            UseWaitCursor = true;
            try
            {
                int code = await Task.Run(() => RunNetsh(cmd));
                if (code == -1223)
                {
                    log("IP 변경: 관리자 권한 요청이 취소되었습니다.");
                    return;
                }
                bool ok = code == 0 || dhcpRadio.Checked; // 이미 DHCP인 경우 netsh가 오류를 반환해도 무시
                log(ok ? $"[완료] '{name}' IP 설정을 {(dhcpRadio.Checked ? "DHCP 자동" : "고정 IP " + ipBox.Text.Trim())}(으)로 변경했습니다."
                       : $"[오류] IP 설정 변경 실패 (코드 {code}). 입력값을 확인하세요.");
                MessageBox.Show(this, ok ? "IP 설정을 적용했습니다." : $"IP 설정 적용에 실패했습니다. (코드 {code})",
                    "IP 설정", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Error);
                await Task.Delay(1500); // 새 주소 반영 대기
                int sel = adapterCombo.SelectedIndex;
                LoadAdapters();
                if (sel >= 0 && sel < adapterCombo.Items.Count) adapterCombo.SelectedIndex = sel;
            }
            finally
            {
                UseWaitCursor = false;
                applyBtn.Enabled = adapters.Count > 0;
            }
        }

        /// <summary>netsh 실행 (필요하면 UAC 요청). -1223 = 사용자가 취소</summary>
        private int RunNetsh(string cmd) => SystemTools.RunElevated(cmd, line => log("  " + line));
    }
}
