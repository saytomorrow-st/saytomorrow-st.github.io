using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
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
        private readonly DataGridView autotuneGrid;
        private readonly Button runAllAutotuneButton;
        private readonly Button refreshAutotuneButton;
        private readonly List<AutotuneTaskDefinition> autotuneTasks;
        private readonly Dictionary<string, DataGridViewRow> autotuneItems;
        private readonly Timer defenderTimer;
        private bool autotuneRunning;

        public MainForm()
        {
            Text = "SayTomorrow Utility";
            MinimumSize = new Size(920, 620);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F);
            autotuneTasks = AutotuneManager.CreateTasks();
            autotuneItems = new Dictionary<string, DataGridViewRow>();

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
            Panel autotuneTop = new Panel { Dock = DockStyle.Top, Height = 70 };
            Label autotuneHint = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "Перед запуском каждая задача проверяет текущий статус. Автонастройка выполняет задачи сверху вниз."
            };
            runAllAutotuneButton = new Button
            {
                Dock = DockStyle.Right,
                Width = 170,
                Text = "Автонастройка"
            };
            runAllAutotuneButton.Click += async delegate { await RunAllAutotuneAsync(); };
            refreshAutotuneButton = new Button
            {
                Dock = DockStyle.Right,
                Width = 150,
                Text = "Проверить"
            };
            refreshAutotuneButton.Click += async delegate { await RefreshAutotuneStatusesAsync(); };
            autotuneTop.Controls.Add(autotuneHint);
            autotuneTop.Controls.Add(refreshAutotuneButton);
            autotuneTop.Controls.Add(runAllAutotuneButton);

            autotuneGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
                BackgroundColor = SystemColors.Window,
                BorderStyle = BorderStyle.Fixed3D,
                ReadOnly = true,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };
            autotuneGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Task", HeaderText = "Задача", Width = 390 });
            autotuneGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "Статус", Width = 150 });
            autotuneGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Details", HeaderText = "Детали", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            autotuneGrid.Columns.Add(new DataGridViewButtonColumn { Name = "Run", HeaderText = "Запуск", Text = "Запустить", UseColumnTextForButtonValue = true, Width = 105 });
            autotuneGrid.CellContentClick += async delegate(object sender, DataGridViewCellEventArgs e) { await AutotuneGridCellContentClickAsync(e); };

            autotuneTab.Controls.Add(autotuneGrid);
            autotuneTab.Controls.Add(autotuneTop);
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
                BuildAutotuneList();
                await RefreshAutotuneStatusesAsync();
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

        private void BuildAutotuneList()
        {
            autotuneGrid.SuspendLayout();
            try
            {
                autotuneGrid.Rows.Clear();
                autotuneItems.Clear();
                foreach (AutotuneTaskDefinition task in autotuneTasks)
                {
                    int rowIndex = autotuneGrid.Rows.Add(task.Title, "не проверено", "", "Запустить");
                    DataGridViewRow row = autotuneGrid.Rows[rowIndex];
                    row.Tag = task;
                    autotuneItems.Add(task.Id, row);
                }
            }
            finally
            {
                autotuneGrid.ResumeLayout();
            }
        }

        private async Task RefreshAutotuneStatusesAsync()
        {
            if (autotuneRunning)
                return;

            SetAutotuneButtons(false);
            try
            {
                foreach (AutotuneTaskDefinition task in autotuneTasks)
                {
                    SetAutotuneStatus(task, "проверка...", "");
                    AutotuneResult result = await Task.Run(delegate { return task.Check(); });
                    SetAutotuneStatus(task, result.Status, result.Details);
                }
            }
            finally
            {
                SetAutotuneButtons(true);
            }
        }

        private async Task AutotuneGridCellContentClickAsync(DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || autotuneRunning)
                return;

            if (autotuneGrid.Columns[e.ColumnIndex].Name != "Run")
                return;

            AutotuneTaskDefinition task = autotuneGrid.Rows[e.RowIndex].Tag as AutotuneTaskDefinition;
            if (task != null)
                await RunSingleAutotuneTaskAsync(task);
        }

        private async Task RunAllAutotuneAsync()
        {
            DefenderStatus defender = await Task.Run(delegate { return DefenderMonitor.Query(); });
            if (defender.RealTimeProtectionEnabled == true)
            {
                DialogResult answer = MessageBox.Show(
                    "Защита в реальном времени Microsoft Defender включена. Некоторые silent-установщики из папки extra могут блокироваться. Временно отключить её можно кнопкой \"Перейти в Защитник Windows\".\n\nПродолжить автонастройку сейчас?",
                    "Предупреждение",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (answer != DialogResult.Yes)
                    return;
            }

            DialogResult confirm = MessageBox.Show(
                "Автонастройка запустит системные изменения от имени администратора: сеть, параметры Windows Update, silent-установщики, тему, обои и разметку только полностью пустых RAW-дисков.\n\nПродолжить?",
                "Автонастройка",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes)
                return;

            autotuneRunning = true;
            SetAutotuneButtons(false);
            try
            {
                foreach (AutotuneTaskDefinition task in autotuneTasks)
                    await RunAutotuneTaskCoreAsync(task, false);
            }
            finally
            {
                autotuneRunning = false;
                SetAutotuneButtons(true);
            }
        }

        private async Task RunSingleAutotuneTaskAsync(AutotuneTaskDefinition task)
        {
            if (task.Destructive)
            {
                DialogResult confirm = MessageBox.Show(
                    "Эта задача размечает только полностью пустые RAW-диски, но всё равно меняет накопители. Проверь, что подключены только нужные диски.\n\nЗапустить?",
                    task.Title,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (confirm != DialogResult.Yes)
                    return;
            }

            autotuneRunning = true;
            SetAutotuneButtons(false);
            try
            {
                await RunAutotuneTaskCoreAsync(task, true);
            }
            finally
            {
                autotuneRunning = false;
                SetAutotuneButtons(true);
            }
        }

        private async Task RunAutotuneTaskCoreAsync(AutotuneTaskDefinition task, bool forceRun)
        {
            SetAutotuneStatus(task, "проверка...", "");
            AutotuneResult check = await Task.Run(delegate { return task.Check(); });
            if (check.Done && !forceRun)
            {
                SetAutotuneStatus(task, check.Status, check.Details);
                return;
            }

            SetAutotuneStatus(task, "выполняется...", check.Details);
            AutotuneResult result = await Task.Run(delegate { return task.Run(); });
            SetAutotuneStatus(task, result.Status, result.Details);
        }

        private void SetAutotuneStatus(AutotuneTaskDefinition task, string status, string details)
        {
            DataGridViewRow row;
            if (!autotuneItems.TryGetValue(task.Id, out row))
                return;

            row.Cells["Status"].Value = status;
            row.Cells["Details"].Value = details ?? string.Empty;
            row.DefaultCellStyle.ForeColor = StatusColor(status);
        }

        private void SetAutotuneButtons(bool enabled)
        {
            runAllAutotuneButton.Enabled = enabled;
            refreshAutotuneButton.Enabled = enabled;
        }

        private static Color StatusColor(string status)
        {
            if (string.IsNullOrEmpty(status))
                return SystemColors.WindowText;

            string normalized = status.ToLowerInvariant();
            if (normalized.Contains("выполнено"))
                return Color.FromArgb(0, 120, 0);
            if (normalized.Contains("ошибка") || normalized.Contains("нет ") || normalized.Contains("не "))
                return Color.FromArgb(190, 30, 30);
            if (normalized.Contains("частично") || normalized.Contains("требуется") || normalized.Contains("пропущено"))
                return Color.DarkOrange;

            return SystemColors.WindowText;
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
