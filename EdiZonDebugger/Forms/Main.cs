﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.IO;
using System.Text.RegularExpressions;

using EdiZonDebugger.Models;
using EdiZonDebugger.Helper;

using vJine.Lua;

namespace EdiZonDebugger
{
    public partial class Main : Form
    {
        string _scriptFolder = "script";
        string _configFolder = "config";
        string _saveFolder = "save";

        Dictionary<string, string> _saveFilePath = null;
        Dictionary<string, string> _luaScriptPath = null;

        Dictionary<string, EdiZonConfig.VersionConfig> _config = null;
        Dictionary<string, LuaContext> _luaInstance = null;

        string _currentVersion = null;

        public Main(string file)
        {
            InitializeComponent();

            if (!Directory.Exists(_scriptFolder))
                Directory.CreateDirectory(_scriptFolder);
            if (!Directory.Exists(_configFolder))
                Directory.CreateDirectory(_configFolder);
            if (!Directory.Exists(_saveFolder))
                Directory.CreateDirectory(_saveFolder);

            if (file != null && Support.TryParseJObject(File.ReadAllText(file)))
                InitDebugger(file);

            UpdateUI();
        }

        #region Events
        private void openToolStripMenuItem_Click(object sender, EventArgs e)
        {
            CloseConfig();

            var of = new OpenFileDialog();
            of.Filter = "(*.json)|*.json";
            of.InitialDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _configFolder);

            if (of.ShowDialog() == DialogResult.OK && File.Exists(of.FileName))
            {
                errorTextBox.Clear();
                InitDebugger(of.FileName);
            }
        }

        private void closeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            CloseConfig();
        }

        private void extractEditedSaveToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var sf = new SaveFileDialog();

            if (sf.ShowDialog() == DialogResult.OK && File.Exists(sf.FileName))
            {
                var save = Lua.GetModifiedSaveBuffer(_luaInstance[_currentVersion]);
                File.WriteAllBytes(sf.FileName, save);
            }
        }

        private void versionComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            _currentVersion = (string)versionComboBox.SelectedItem;
            UpdateCategories();
        }

        private void categoriesListBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            UpdateItems();
        }
        #endregion

        #region Functions
        private void UpdateUI()
        {
            var opened = _config != null && _luaInstance != null;

            closeToolStripMenuItem.Enabled = opened;
            extractEditedSaveToolStripMenuItem.Enabled = opened;
            categoriesListBox.Enabled = opened;
            groupBox1.Enabled = opened;
            versionComboBox.Enabled = opened;
        }

        private void UpdateVersions()
        {
            versionComboBox.SelectedIndexChanged -= versionComboBox_SelectedIndexChanged;

            versionComboBox.Items.Clear();

            foreach (var item in _config)
                versionComboBox.Items.Add(item.Key);

            versionComboBox.SelectedIndexChanged += versionComboBox_SelectedIndexChanged;
            versionComboBox.SelectedIndex = 0;
        }

        private void UpdateCategories()
        {
            categoriesListBox.SelectedIndexChanged -= categoriesListBox_SelectedIndexChanged;

            categoriesListBox.Items.Clear();

            if (_config[_currentVersion].items.Any(i => i.category == null))
                categoriesListBox.Items.Add("No Category");

            foreach (var cat in _config[_currentVersion].items.Where(i => i.category != null).Select(i => i.category).Distinct())
                categoriesListBox.Items.Add(cat);

            categoriesListBox.SelectedIndexChanged += categoriesListBox_SelectedIndexChanged;
            categoriesListBox.SelectedIndex = 0;
        }

        private void UpdateItems()
        {
            groupBox1.Controls.Clear();
            groupBox1.Text = (string)categoriesListBox.SelectedItem;

            var p = new Point(5, 20);

            var panel = new Panel
            {
                AutoScroll = true,
                Location = p,
                Size = new Size(groupBox1.Width - p.X - 10, groupBox1.Height - p.Y - 10),
                Anchor = (AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right)
            };
            groupBox1.Controls.Add(panel);

            foreach (var item in _config[_currentVersion].items.Where(i => i.category == (string)categoriesListBox.SelectedItem))
            {
                AddItem(panel, item, p);
                p = new Point(p.X, p.Y + 30);
            }
        }

        private void CloseConfig()
        {
            _saveFilePath = null;
            _luaScriptPath = null;
            _config = null;
            _luaInstance = null;

            groupBox1.Controls.Clear();
            groupBox1.Text = "";

            categoriesListBox.Items.Clear();
            versionComboBox.Items.Clear();
            versionComboBox.Text = "";

            UpdateUI();
        }

        private void InitDebugger(string configName)
        {
            LogConsole.LogBox = errorTextBox;

            if (!OpenConfig(configName, out var error))
            {
                LogConsole.Instance.Log("Failed to load config file: " + error, LogLevel.FATAL);
                CloseConfig();
                return;
            }
            foreach (var item in _config)
            {
                _currentVersion = item.Key;

                if (!OpenSaveFile(out error))
                {
                    LogConsole.Instance.Log($"Failed to load save file for version \"{item.Key}\":" + error, LogLevel.FATAL);
                    CloseConfig();
                    return;
                }
                if (!OpenScript(out error))
                {
                    LogConsole.Instance.Log($"Failed to load script file for version \"{item.Key}\":" + error, LogLevel.FATAL);
                    CloseConfig();
                    return;
                }
            }

            UpdateUI();
            UpdateVersions();
        }

        private bool OpenConfig(string file, out string message, List<string> searchedJsons = null)
        {
            var content = File.ReadAllText(file);
            if (Support.IsUsingInstead(content, out var config))
            {
                var combPath = Path.Combine(_configFolder, config.useInstead);

                if (searchedJsons == null)
                    searchedJsons = new List<string>();

                if (!searchedJsons.Contains(combPath))
                {
                    searchedJsons.Add(combPath);
                    return OpenConfig(combPath, out message, searchedJsons);
                }
                else
                {
                    message = "UseInstead loop detected.";
                    return false;
                }
            }

            return Support.TryParseConfig(content, out _config, out message);
        }

        private bool OpenSaveFile(out string message)
        {
            message = "";

            if (!CheckConfig(out message))
                return false;

            //Get directories
            var paths = new List<string> { _saveFolder };
            if (_config[_currentVersion].saveFilePaths.Count > 0)
            {
                paths.AddRange(GetSavePaths(_saveFolder, _config[_currentVersion].saveFilePaths.ToArray()).ToList());
                if (!paths.Any())
                {
                    message = "No directories found.";
                    return false;
                }
            }

            //Get files
            var files = GetSaveFiles(paths.ToArray(), _config[_currentVersion].files).ToList();
            if (!files.Any())
            {
                message = "No files found.";
                return false;
            }

            if (_saveFilePath == null)
                _saveFilePath = new Dictionary<string, string>();

            if (files.Count == 1)
                _saveFilePath.Add(_currentVersion, files[0]);
            else
            {
                var selector = new SaveSelector(files.ToArray());
                selector.ShowDialog();
                if (selector.ProperExit)
                    _saveFilePath.Add(_currentVersion, selector.SelectedFile);
                else
                {
                    message = "No file selected.";
                    return false;
                }
            }

            return true;
        }

        private IEnumerable<string> GetSavePaths(string currentPath, string[] saveFilePaths, int depth = 0)
        {
            var tmp = new List<string>();
            foreach (var d in Directory.GetDirectories(currentPath))
            {
                var dirName = d.Split('\\').Last();
                if (Regex.IsMatch(dirName, saveFilePaths[depth]))
                    tmp.Add(Path.Combine(currentPath, dirName));
            }

            if (depth + 1 >= saveFilePaths.Length)
                return tmp;

            var result = new List<string>();
            foreach (var rp in tmp)
                result.AddRange(GetSavePaths(rp, saveFilePaths, depth + 1) ?? new List<string>());

            return result;
        }
        private IEnumerable<string> GetSaveFiles(string[] paths, string files)
        {
            foreach (var path in paths)
                foreach (var file in Directory.GetFiles(path))
                    if (Regex.IsMatch(file, files))
                        yield return file;
        }

        private bool OpenScript(out string message)
        {
            message = "";

            if (!CheckConfig(out message))
                return false;

            if (!SetScriptPath())
            {
                message = "Script path not found";
                return false;
            }

            if (_luaInstance == null)
                _luaInstance = new Dictionary<string, LuaContext>();

            var context = new LuaContext();
            if (!Lua.InitializeScript(ref context, _luaScriptPath[_currentVersion], _saveFilePath[_currentVersion], out var error))
            {
                message = error;
                return false;
            }

            _luaInstance.Add(_currentVersion, context);
            return true;
        }
        private bool SetScriptPath()
        {
            if (_luaScriptPath == null)
                _luaScriptPath = new Dictionary<string, string>();

            var path = Path.Combine(_scriptFolder, $"{_config[_currentVersion].filetype}.lua");

            if (!File.Exists(path))
            {
                LogConsole.Instance.Log($"{_luaScriptPath} cannot be found. Choose a script yourself.", LogLevel.WARNING);

                var of = new OpenFileDialog();
                of.Filter = "(*.lua)|*.lua";
                if (of.ShowDialog() == DialogResult.OK && File.Exists(of.FileName))
                    _luaScriptPath.Add(_currentVersion, of.FileName);
                else return false;
            }
            else
            {
                _luaScriptPath.Add(_currentVersion, path);
            }

            return true;
        }

        private bool CheckConfig(out string message)
        {
            if (_config == null)
            {
                message = "Config not set";
                return false;
            }
            if (!_config.ContainsKey(_currentVersion))
            {
                message = "Version doesn't exist";
                return false;
            }
            if (_config[_currentVersion].filetype == null)
            {
                message = "FileType not set";
                return false;
            }

            message = "";
            return true;
        }

        private void AddItem(Panel panel, EdiZonConfig.VersionConfig.Item item, Point initPoint)
        {
            var label = new Label { Text = item.name + ":", Location = initPoint };
            panel.Controls.Add(label);

            initPoint = new Point(initPoint.X + label.Width + 10, initPoint.Y);

            Control itemControl = null;
            var luaValue = Lua.GetValueFromSaveFile(_luaInstance[_currentVersion], item.strArgs.ToArray(), item.intArgs.ToArray());
            bool validItem = true;
            switch (item.widget.type)
            {
                case "int":
                    validItem = item.widget.minValue <= Convert.ToUInt32(luaValue) && Convert.ToUInt32(luaValue) <= item.widget.maxValue;

                    itemControl = new TextBox
                    {
                        Text = validItem ? Convert.ToString(luaValue) : "???",
                        Enabled = validItem
                    };

                    if (validItem)
                    {
                        (itemControl as TextBox).TextChanged += SetValue_OnChange;
                        (itemControl as TextBox).KeyPress += (s, e) => 
                        {
                            if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar) && (e.KeyChar != '.'))
                            {
                                e.Handled = true;
                            }
                        };
                    }
                    break;
                case "bool":
                    validItem = item.widget.onValue == Convert.ToUInt32(luaValue) || item.widget.offValue == Convert.ToUInt32(luaValue);

                    itemControl = new CheckBox
                    {
                        Text = (validItem) ? "" : "???",
                        Checked = Convert.ToInt32(luaValue) == item.widget.onValue,
                        Enabled = validItem
                    };

                    if (validItem)
                        (itemControl as CheckBox).CheckedChanged += SetValue_OnChange;
                    break;
                case "list":
                    validItem = item.widget.listItemValues.Contains(Convert.ToUInt32(luaValue));

                    itemControl = new ComboBox
                    {
                        DataSource = (validItem) ? item.widget.listItemNames : new List<string> { "???" },
                        SelectedIndex = item.widget.listItemValues.IndexOf(Convert.ToUInt32(luaValue)),
                        Enabled = validItem
                    };

                    if (validItem)
                        (itemControl as ComboBox).SelectedIndexChanged += SetValue_OnChange;
                    break;
            }
            if (!validItem)
                LogConsole.Instance.Log($"Item \"{item.name}\"{((String.IsNullOrEmpty(item.category)) ? "" : $" in Category \"{item.category}\"")} of type \"{item.widget.type}\" has an invalid value of {luaValue.ToString()}.\"\r\n", LogLevel.ERROR);

            itemControl.Tag = item;
            itemControl.Location = initPoint;

            panel.Controls.Add(itemControl);
        }
        private void SetValue_OnChange(object sender, EventArgs e)
        {
            var item = (EdiZonConfig.VersionConfig.Item)((Control)sender).Tag;
            switch (sender)
            {
                case TextBox textBox:
                    textBox.TextChanged -= SetValue_OnChange;

                    if (!String.IsNullOrEmpty(textBox.Text) && textBox.Text.IsNumeric())
                    {
                        textBox.Text = Math.Min(Math.Max(Convert.ToInt32(textBox.Text), item.widget.minValue), item.widget.maxValue).ToString();
                        Lua.SetValueInSaveFile(_luaInstance[_currentVersion], item.strArgs.ToArray(), item.intArgs.ToArray(), Convert.ToInt32(textBox.Text));
                    }

                    textBox.TextChanged += SetValue_OnChange;
                    break;
                case ComboBox comboBox:
                    Lua.SetValueInSaveFile(_luaInstance[_currentVersion], item.strArgs.ToArray(), item.intArgs.ToArray(), comboBox.Enabled ? item.widget.onValue : item.widget.offValue);
                    break;
                case ListBox listBox:
                    Lua.SetValueInSaveFile(_luaInstance[_currentVersion], item.strArgs.ToArray(), item.intArgs.ToArray(), item.widget.listItemValues[listBox.SelectedIndex]);
                    break;
            }
        }

        #endregion
    }
}
