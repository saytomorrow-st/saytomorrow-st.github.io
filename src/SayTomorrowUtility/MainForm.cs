using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SayTomorrowUtility
{
    internal sealed class MainForm : Form
    {
        private readonly Label defenderStatusLabel;
        private readonly Button defenderButton;
        private readonly Button refreshButton;
        private readonly ListView informationList;
        private readonly Timer defenderTimer;

        public MainForm()
        {
            Text = "SayTomorrow Utility";
            MinimumSize = new Size(920, 620);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F);

            Panel defenderPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 48,
                Padding = new Padding(12, 8, 12, 8),
                BackColor = Color.FromArgb(245, 247, 250)
            };

            defenderStatusLabel = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "Защитник Windows: проверка..."
            };

            defenderButton = new Button
            {
                Dock = DockStyle.Right,
                Width = 245,
                Text = "Перейти в Защитник Windows"
            };
            defenderButton.Click += delegate { OpenDefender(); };

            defenderPanel.Controls.Add(defenderStatusLabel);
            defenderPanel.Controls.Add(defenderButton);

            TabControl tabs = new TabControl
            {
                Dock = DockStyle.Fill,
                Padding = new Point(16, 6)
            };

            TabPage informationTab = new TabPage("Информация") { Padding = new Padding(8) };
            TabPage autotuneTab = new TabPage("Автонастройка") { Padding = new Padding(8) };
            TabPage diagnosticsTab = new TabPage("Диагностика") { Padding = new Padding(8) };

            Panel informationTop = new Panel { Dock = DockStyle.Top, Height = 42 };
            Label informationHint = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "Сводная информация о компьютере. Внешние USB-накопители отфильтрованы."
            };
            refreshButton = new Button
            {
                Dock = DockStyle.Right,
                Width = 150,
                Text = "Обновить"
            };
            refreshButton.Click += async delegate { await LoadSystemInfoAsync(); };
            informationTop.Controls.Add(informationHint);
            informationTop.Controls.Add(refreshButton);

            informationList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                HideSelection = false
            };
            informationList.Columns.Add("Параметр", 330);
            informationList.Columns.Add("Значение", 520);

            informationTab.Controls.Add(informationList);
            informationTab.Controls.Add(informationTop);
            autotuneTab.Controls.Add(CreateEmptyTabLabel("Вкладка автонастройки пока пустая."));
            diagnosticsTab.Controls.Add(CreateEmptyTabLabel("Вкладка диагностики пока пустая."));

            tabs.TabPages.Add(informationTab);
            tabs.TabPages.Add(autotuneTab);
            tabs.TabPages.Add(diagnosticsTab);

            Controls.Add(tabs);
            Controls.Add(defenderPanel);

            defenderTimer = new Timer { Interval = 3000 };
            defenderTimer.Tick += async delegate { await RefreshDefenderStatusAsync(); };

            Shown += async delegate
            {
                defenderTimer.Start();
                await RefreshDefenderStatusAsync();
                await LoadSystemInfoAsync();
            };
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            defenderTimer.Stop();
            defenderTimer.Dispose();
            base.OnFormClosed(e);
        }

        private static Control CreateEmptyTabLabel(string text)
        {
            return new Label
            {
                Dock = DockStyle.Fill,
                Text = text,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.FromArgb(110, 110, 110)
            };
        }

        private async Task LoadSystemInfoAsync()
        {
            refreshButton.Enabled = false;
            informationList.BeginUpdate();
            try
            {
                informationList.Items.Clear();
                informationList.Groups.Clear();
                informationList.Items.Add(new ListViewItem(new[] { "Статус", "Сбор данных..." }));
            }
            finally
            {
                informationList.EndUpdate();
            }

            SystemInfoSnapshot snapshot = await Task.Run(delegate { return SystemInfoCollector.Collect(); });

            informationList.BeginUpdate();
            try
            {
                informationList.Items.Clear();
                informationList.Groups.Clear();

                Dictionary<string, ListViewGroup> groups = new Dictionary<string, ListViewGroup>();
                foreach (InfoRow row in snapshot.Rows)
                {
                    ListViewGroup group;
                    if (!groups.TryGetValue(row.Section, out group))
                    {
                        group = new ListViewGroup(row.Section, HorizontalAlignment.Left);
                        groups.Add(row.Section, group);
                        informationList.Groups.Add(group);
                    }

                    ListViewItem item = new ListViewItem(row.Name, group);
                    item.SubItems.Add(row.Value);
                    informationList.Items.Add(item);
                }

                informationList.AutoResizeColumns(ColumnHeaderAutoResizeStyle.HeaderSize);
            }
            finally
            {
                informationList.EndUpdate();
                refreshButton.Enabled = true;
            }
        }

        private async Task RefreshDefenderStatusAsync()
        {
            DefenderStatus status = await Task.Run(delegate { return DefenderMonitor.Query(); });
            if (!status.Available)
            {
                defenderStatusLabel.ForeColor = Color.DarkOrange;
                defenderStatusLabel.Text = "Защитник Windows: состояние недоступно" + (string.IsNullOrWhiteSpace(status.Error) ? string.Empty : " (" + status.Error + ")");
                return;
            }

            bool protectedNow = status.RealTimeProtectionEnabled == true && status.AntivirusEnabled != false && status.ServiceEnabled != false;
            defenderStatusLabel.ForeColor = protectedNow ? Color.FromArgb(0, 120, 0) : Color.FromArgb(190, 30, 30);
            defenderStatusLabel.Text = string.Format(
                "{0}: защита в реальном времени — {1}, антивирус — {2}, служба — {3}",
                string.IsNullOrWhiteSpace(status.ProductName) ? "Защитник Windows" : status.ProductName,
                FormatBool(status.RealTimeProtectionEnabled),
                FormatBool(status.AntivirusEnabled),
                FormatBool(status.ServiceEnabled));
        }

        private static string FormatBool(bool? value)
        {
            if (!value.HasValue)
                return "н/д";

            return value.Value ? "включено" : "выключено";
        }

        private static void OpenDefender()
        {
            try
            {
                DefenderMonitor.OpenWindowsSecurity();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось открыть Защитник Windows: " + ex.Message, "SayTomorrow Utility", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
