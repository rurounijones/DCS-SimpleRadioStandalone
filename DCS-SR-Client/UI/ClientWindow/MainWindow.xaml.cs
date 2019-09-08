﻿using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Audio;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Audio.Managers;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Input;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Network;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Preferences;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Settings;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Singletons;
using Ciribob.DCS.SimpleRadio.Standalone.Client.UI.ClientWindow.Favourites;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Utils;
using Ciribob.DCS.SimpleRadio.Standalone.Common;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Network;
using MahApps.Metro.Controls;
using NAudio.CoreAudioApi;
using NLog;
using WPFCustomMessageBox;

namespace Ciribob.DCS.SimpleRadio.Standalone.Client.UI
{
    /// <summary>
    ///     Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : MetroWindow
    {
        public delegate void ReceivedAutoConnect(string address, int port);

        public delegate void ToggleOverlayCallback(bool uiButton);

        private AudioManager _audioManager;

        private readonly ConcurrentDictionary<string, SRClient> _clients = new ConcurrentDictionary<string, SRClient>();

        private readonly string _guid;
        private readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private AudioPreview _audioPreview;
        private ClientSync _client;
        private int _port = 5002;

        private Overlay.RadioOverlayWindow _radioOverlayWindow;
        private AwacsRadioOverlayWindow.RadioOverlayWindow _awacsRadioOverlay;

        private IPAddress _resolvedIp;
        private ServerSettingsWindow _serverSettingsWindow;

        //used to debounce toggle
        private long _toggleShowHide;

        private readonly DispatcherTimer _updateTimer;
        private readonly DispatcherTimer _redrawUITimer;
        private MMDeviceCollection outputDeviceList;
        private ServerAddress _serverAddress;
        private readonly DelegateCommand _connectCommand;

        private readonly SettingsStore _settings = SettingsStore.Instance;
        private readonly ClientStateSingleton _clientStateSingleton = ClientStateSingleton.Instance;
        private readonly SyncedServerSettings _serverSettings = SyncedServerSettings.Instance;

        public MainWindow()
        {
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

            InitializeComponent();

            // Initialize ToolTip controls
            ToolTips.Init();

            // Initialize images/icons
            Images.Init();

            // Initialise sounds
            Sounds.Init();

            // Set up tooltips that are always defined
            InitToolTips();

            DataContext = this;

            var client = ClientStateSingleton.Instance;

            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = _settings.GetPositionSetting(SettingsKeys.ClientX).DoubleValue;
            Top = _settings.GetPositionSetting(SettingsKeys.ClientY).DoubleValue;

            Title = Title + " - " + UpdaterChecker.VERSION;

            CheckWindowVisibility();

            if (_settings.GetClientSetting(SettingsKeys.StartMinimised).BoolValue)
            {
                Hide();
                WindowState = WindowState.Minimized;

                Logger.Info("Started DCS-SimpleRadio Client " + UpdaterChecker.VERSION + " minimized");
            }
            else
            {
                Logger.Info("Started DCS-SimpleRadio Client " + UpdaterChecker.VERSION);
            }

            _guid = _settings.GetClientSetting(SettingsKeys.CliendIdShort).StringValue;

            Analytics.Log("Client", "Startup", _settings.GetClientSetting(SettingsKeys.ClientIdLong).RawValue);

            InitSettingsScreen();

            _connectCommand = new DelegateCommand(Connect, () => ServerAddress != null);
            FavouriteServersViewModel = new FavouriteServersViewModel(new CsvFavouriteServerStore());

            InitDefaultAddress();

            ExternalAWACSModeName.Text = _settings.GetClientSetting(SettingsKeys.LastSeenName).StringValue;

            UpdaterChecker.CheckForUpdate(_settings.GetClientSetting(SettingsKeys.CheckForBetaUpdates).BoolValue);

            _redrawUITimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _redrawUITimer.Tick += RedrawUITick;
            _redrawUITimer.Start();

            Connect();
        }

        private void CheckWindowVisibility()
        {
            if (_settings.GetPositionSetting(SettingsKeys.DisableWindowVisibilityCheck).BoolValue)
            {
                Logger.Info("Window visibility check is disabled, skipping");
                return;
            }

            bool mainWindowVisible = false;
            bool radioWindowVisible = false;
            bool awacsWindowVisible = false;

            int mainWindowX = (int)_settings.GetPositionSetting(SettingsKeys.ClientX).DoubleValue;
            int mainWindowY = (int)_settings.GetPositionSetting(SettingsKeys.ClientY).DoubleValue;
            int radioWindowX = (int)_settings.GetPositionSetting(SettingsKeys.RadioX).DoubleValue;
            int radioWindowY = (int)_settings.GetPositionSetting(SettingsKeys.RadioY).DoubleValue;
            int awacsWindowX = (int)_settings.GetPositionSetting(SettingsKeys.AwacsX).DoubleValue;
            int awacsWindowY = (int)_settings.GetPositionSetting(SettingsKeys.AwacsY).DoubleValue;

            Logger.Info($"Checking window visibility for main client window {{X={mainWindowX},Y={mainWindowY}}}");
            Logger.Info($"Checking window visibility for radio overlay {{X={radioWindowX},Y={radioWindowY}}}");
            Logger.Info($"Checking window visibility for AWACS overlay {{X={awacsWindowX},Y={awacsWindowY}}}");

            foreach (System.Windows.Forms.Screen screen in System.Windows.Forms.Screen.AllScreens)
            {
                Logger.Info($"Checking {(screen.Primary ? "primary " : "")}screen {screen.DeviceName} with bounds {screen.Bounds} for window visibility");

                if (screen.Bounds.Contains(mainWindowX, mainWindowY))
                {
                    Logger.Info($"Main client window {{X={mainWindowX},Y={mainWindowY}}} is visible on {(screen.Primary ? "primary " : "")}screen {screen.DeviceName} with bounds {screen.Bounds}");
                    mainWindowVisible = true;
                }
                if (screen.Bounds.Contains(radioWindowX, radioWindowY))
                {
                    Logger.Info($"Radio overlay {{X={radioWindowX},Y={radioWindowY}}} is visible on {(screen.Primary ? "primary " : "")}screen {screen.DeviceName} with bounds {screen.Bounds}");
                    radioWindowVisible = true;
                }
                if (screen.Bounds.Contains(awacsWindowX, awacsWindowY))
                {
                    Logger.Info($"AWACS overlay {{X={awacsWindowX},Y={awacsWindowY}}} is visible on {(screen.Primary ? "primary " : "")}screen {screen.DeviceName} with bounds {screen.Bounds}");
                    awacsWindowVisible = true;
                }
            }

            if (!mainWindowVisible)
            {
                MessageBox.Show(this,
                    "The SRS client window is no longer visible likely due to a monitor reconfiguration.\n\nThe position will be reset to default to fix this issue.",
                    "SRS window position reset",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                Logger.Warn($"Main client window outside visible area of monitors, resetting position ({mainWindowX},{mainWindowY}) to defaults");

                _settings.SetPositionSetting(SettingsKeys.ClientX, 200);
                _settings.SetPositionSetting(SettingsKeys.ClientY, 200);

                Left = 200;
                Top = 200;
            }

            if (!radioWindowVisible)
            {
                MessageBox.Show(this,
                    "The SRS radio overlay is no longer visible likely due to a monitor reconfiguration.\n\nThe position will be reset to default to fix this issue.",
                    "SRS window position reset",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                Logger.Warn($"Radio overlay window outside visible area of monitors, resetting position ({radioWindowX},{radioWindowY}) to defaults");

                _settings.SetPositionSetting(SettingsKeys.RadioX, 300);
                _settings.SetPositionSetting(SettingsKeys.RadioY, 300);

                if (_radioOverlayWindow != null)
                {
                    _radioOverlayWindow.Left = 300;
                    _radioOverlayWindow.Top = 300;
                }
            }

            if (!awacsWindowVisible)
            {
                MessageBox.Show(this,
                    "The SRS AWACS overlay is no longer visible likely due to a monitor reconfiguration.\n\nThe position will be reset to default to fix this issue",
                    "SRS window position reset",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                Logger.Warn($"AWACS overlay window outside visible area of monitors, resetting position ({awacsWindowX},{awacsWindowY}) to defaults");

                _settings.SetPositionSetting(SettingsKeys.AwacsX, 300);
                _settings.SetPositionSetting(SettingsKeys.AwacsY, 300);

                if (_awacsRadioOverlay != null)
                {
                    _awacsRadioOverlay.Left = 300;
                    _awacsRadioOverlay.Top = 300;
                }
            }

            if (!mainWindowVisible || !radioWindowVisible || !awacsWindowVisible)
            {
                _settings.Save();
            }
        }

        private void InitDefaultAddress()
        {
            // legacy setting migration
            if (!string.IsNullOrEmpty(_settings.GetClientSetting(SettingsKeys.LastServer).StringValue) &&
                FavouriteServersViewModel.Addresses.Count == 0)
            {
                var oldAddress = new ServerAddress(_settings.GetClientSetting(SettingsKeys.LastServer).StringValue,
                    _settings.GetClientSetting(SettingsKeys.LastServer).StringValue, null, true);
                FavouriteServersViewModel.Addresses.Add(oldAddress);
            }

            ServerAddress = FavouriteServersViewModel.DefaultServerAddress;
        }

        private void InitToolTips()
        {
            ExternalAWACSModePassword.ToolTip = ToolTips.ExternalAWACSModePassword;
            ExternalAWACSModeName.ToolTip = ToolTips.ExternalAWACSModeName;
            ConnectExternalAWACSMode.ToolTip = ToolTips.ExternalAWACSMode;
        }

        public InputDeviceManager InputManager { get; set; }

        public FavouriteServersViewModel FavouriteServersViewModel { get; }

        public ServerAddress ServerAddress
        {
            get { return _serverAddress; }
            set
            {
                _serverAddress = value;
                if (value != null)
                {
                    ServerIp.Text = value.Address;
                    ExternalAWACSModePassword.Password = string.IsNullOrWhiteSpace(value.EAMCoalitionPassword) ? "" : value.EAMCoalitionPassword;
                }
                
                _connectCommand.RaiseCanExecuteChanged();
            }
        }

        public ICommand ConnectCommand => _connectCommand;

        private void RedrawUITick(object sender, EventArgs e)
        {
            bool isGameGuiConnected = _clientStateSingleton.IsGameGuiConnected;
            bool isGameExportConnected = _clientStateSingleton.IsGameExportConnected;

            // Redraw UI state (currently once per second), toggling controls as required
            // Some other callbacks/UI state changes could also probably be moved to this...
            if (_clientStateSingleton.IsConnected)
            {
                bool eamEnabled = _serverSettings.GetSettingAsBool(Common.Setting.ServerSettingsKeys.EXTERNAL_AWACS_MODE);

                ExternalAWACSModePassword.IsEnabled = eamEnabled && !_clientStateSingleton.InExternalAWACSMode && !isGameExportConnected;
                ExternalAWACSModeName.IsEnabled = eamEnabled && !_clientStateSingleton.InExternalAWACSMode && !isGameExportConnected;
            }
            else
            {
                ExternalAWACSModePassword.IsEnabled = false;
                ExternalAWACSModeName.IsEnabled = false;
            }
        }


        private void InitSettingsScreen()
        {
            RadioEncryptionEffectsToggle.IsChecked =
                _settings.GetClientSetting(SettingsKeys.RadioEncryptionEffects).BoolValue;
            RadioSwitchIsPTT.IsChecked =
                _settings.GetClientSetting(SettingsKeys.RadioSwitchIsPTT).BoolValue;
            AutoConnectPromptToggle.IsChecked = _settings.GetClientSetting(SettingsKeys.AutoConnectPrompt).BoolValue;
            AutoConnectMismatchPromptToggle.IsChecked = _settings.GetClientSetting(SettingsKeys.AutoConnectMismatchPrompt).BoolValue;
            RadioOverlayTaskbarItem.IsChecked =
                _settings.GetClientSetting(SettingsKeys.RadioOverlayTaskbarHide).BoolValue;
            RefocusDCS.IsChecked = _settings.GetClientSetting(SettingsKeys.RefocusDCS).BoolValue;
            ExpandInputDevices.IsChecked = _settings.GetClientSetting(SettingsKeys.ExpandControls).BoolValue;
            RadioTxStartToggle.IsChecked = _settings.GetClientSetting(SettingsKeys.RadioTxEffects_Start).BoolValue;
            RadioTxEndToggle.IsChecked = _settings.GetClientSetting(SettingsKeys.RadioTxEffects_End).BoolValue;
            
            RadioRxStartToggle.IsChecked = _settings.GetClientSetting(SettingsKeys.RadioRxEffects_Start).BoolValue;
            RadioRxEndToggle.IsChecked = _settings.GetClientSetting(SettingsKeys.RadioRxEffects_Start).BoolValue;

            MinimiseToTray.IsChecked = _settings.GetClientSetting(SettingsKeys.MinimiseToTray).BoolValue;
            StartMinimised.IsChecked = _settings.GetClientSetting(SettingsKeys.StartMinimised).BoolValue;

            RadioSoundEffects.IsChecked = _settings.GetClientSetting(SettingsKeys.RadioEffects).BoolValue;
            RadioSoundEffectsClipping.IsChecked = _settings.GetClientSetting(SettingsKeys.RadioEffectsClipping).BoolValue;
            AutoSelectChannel.IsChecked = _settings.GetClientSetting(SettingsKeys.AutoSelectPresetChannel).BoolValue;

            AlwaysAllowHotas.IsChecked = _settings.GetClientSetting(SettingsKeys.AlwaysAllowHotasControls).BoolValue;
            AllowDCSPTT.IsChecked = _settings.GetClientSetting(SettingsKeys.AllowDCSPTT).BoolValue;

            CheckForBetaUpdates.IsChecked = _settings.GetClientSetting(SettingsKeys.CheckForBetaUpdates).BoolValue;
            PlayConnectionSounds.IsChecked = _settings.GetClientSetting(SettingsKeys.PlayConnectionSounds).BoolValue;
        }

        private void Connect()
        {
            if (_clientStateSingleton.IsConnected)
            {
                Stop();
            }
            else
            {
                SaveSelectedInputAndOutput();

                _audioManager = new AudioManager(_clients);

                try
                {
                    //process hostname
                    var resolvedAddresses = Dns.GetHostAddresses(GetAddressFromTextBox());
                    var ip = resolvedAddresses.FirstOrDefault(xa => xa.AddressFamily == AddressFamily.InterNetwork); // Ensure we get an IPv4 address in case the host resolves to both IPv6 and IPv4

                    if (ip != null)
                    {
                        _resolvedIp = ip;
                        _port = GetPortFromTextBox();

                        _client = new ClientSync(_clients, _guid, UpdateUICallback);
                        _client.TryConnect(new IPEndPoint(_resolvedIp, _port), ConnectCallback);

                        StartStop.Content = "Connecting...";
                        StartStop.IsEnabled = false;
                    }
                    else
                    {
                        //invalid ID
                        MessageBox.Show("Invalid IP or Host Name!", "Host Name Error", MessageBoxButton.OK,
                            MessageBoxImage.Error);

                        _clientStateSingleton.IsConnected = false;
                        ToggleServerSettings.IsEnabled = false;
                    }
                }
                catch (Exception ex) when (ex is SocketException || ex is ArgumentException)
                {
                    MessageBox.Show("Invalid IP or Host Name!", "Host Name Error", MessageBoxButton.OK,
                        MessageBoxImage.Error);

                    _clientStateSingleton.IsConnected = false;
                    ToggleServerSettings.IsEnabled = false;
                }
            }
        }

        private string GetAddressFromTextBox()
        {
            var addr = ServerIp.Text.Trim();

            if (addr.Contains(":"))
            {
                return addr.Split(':')[0];
            }

            return addr;
        }

        private int GetPortFromTextBox()
        {
            var addr = ServerIp.Text.Trim();

            if (addr.Contains(":"))
            {
                int port;
                if (int.TryParse(addr.Split(':')[1], out port))
                {
                    return port;
                }
                throw new ArgumentException("specified port is not valid");
            }

            return 5002;
        }

        private void Stop(bool connectionError = false)
        {
            if (_clientStateSingleton.IsConnected && _settings.GetClientSetting(SettingsKeys.PlayConnectionSounds).BoolValue)
            {
                try
                {
                    Sounds.BeepDisconnected.Play();
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Failed to play disconnect sound");
                }
            }

            StartStop.Content = "Connect";
            StartStop.IsEnabled = true;
            _clientStateSingleton.IsConnected = false;
            ToggleServerSettings.IsEnabled = false;

            ServerConnectionStatus.Source = connectionError ? Images.IconDisconnectedError : Images.IconDisconnected;
            VOIPConnectionStatus.Source = connectionError ? Images.IconDisconnectedError : Images.IconDisconnected;

            ExternalAWACSModePassword.IsEnabled = false;
            ExternalAWACSModePasswordLabel.IsEnabled = false;
            ExternalAWACSModeName.IsEnabled = false;
            ExternalAWACSModeNameLabel.IsEnabled = false;
            ConnectExternalAWACSMode.IsEnabled = false;
            ConnectExternalAWACSMode.Content = "Connect External AWACS MODE (EAM)";

            if (!string.IsNullOrWhiteSpace(_clientStateSingleton.LastSeenName) &&
                _settings.GetClientSetting(SettingsKeys.LastSeenName).StringValue != _clientStateSingleton.LastSeenName)
            {
                _settings.SetClientSetting(SettingsKeys.LastSeenName, _clientStateSingleton.LastSeenName);
            }

            try
            {
                if (_audioManager != null)
                {
                    _audioManager.StopEncoding();
                    _audioManager = null;
                }
            }
            catch (Exception ex)
            {
            }

            if (_client != null)
            {
                _client.Disconnect();
                _client = null;
            }

            _clientStateSingleton.DcsPlayerRadioInfo.Reset();
            _clientStateSingleton.DcsPlayerSideInfo.Reset();
        }

        private void SaveSelectedInputAndOutput()
        {
        }

        private void ConnectCallback(bool result, bool connectionError, string connection)
        {
            string currentConnection = ServerIp.Text.Trim();
            if (!currentConnection.Contains(":"))
            {
                currentConnection += ":5002";
            }

            if (result)
            {
                if (!_clientStateSingleton.IsConnected)
                {
                    try
                    {

                        StartStop.Content = "Disconnect";
                        StartStop.IsEnabled = true;

                        _clientStateSingleton.IsConnected = true;
                        ServerConnectionStatus.Source = Images.IconConnected;
                        VOIPConnectionStatus.Source = Images.IconDisconnected;

                        _settings.SetClientSetting(SettingsKeys.LastServer, ServerIp.Text);

                        _audioManager.StartEncoding(-1, null, _guid, InputManager,
                            _resolvedIp, _port, null, VOIPConnectCallback);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex,
                            "Unable to get audio device - likely output device error - Pick another. Error:" +
                            ex.Message);
                        Stop();

                        var messageBoxResult = CustomMessageBox.ShowYesNo(
                            "Problem initialising Audio Output!\n\nTry a different Output device and please post your clientlog.txt to the support Discord server.\n\nJoin support Discord server now?",
                            "Audio Output Error",
                            "OPEN PRIVACY SETTINGS",
                            "JOIN DISCORD SERVER",
                            MessageBoxImage.Error);

                        if (messageBoxResult == MessageBoxResult.Yes) Process.Start("https://discord.gg/baw7g3t");
                    }
                }
            }
            else if (string.Equals(currentConnection, connection, StringComparison.OrdinalIgnoreCase))
            {
                // Only stop connection/reset state if connection is currently active
                // Autoconnect mismatch will quickly disconnect/reconnect, leading to double-callbacks
                Stop(connectionError);
            }
            else
            {
                if (!_clientStateSingleton.IsConnected)
                {
                    Stop(connectionError);
                }
            }
        }

        private void VOIPConnectCallback(bool result, bool connectionError, string connection)
        {
            if (result)
            {
                VOIPConnectionStatus.Source = Images.IconConnected;
            }
            else
            {
                VOIPConnectionStatus.Source = connectionError ? Images.IconDisconnectedError : Images.IconDisconnected;
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            _settings.SetPositionSetting(SettingsKeys.ClientX, Left);
            _settings.SetPositionSetting(SettingsKeys.ClientY, Top);

            if (!string.IsNullOrWhiteSpace(_clientStateSingleton.LastSeenName) &&
                _settings.GetClientSetting(SettingsKeys.LastSeenName).StringValue != _clientStateSingleton.LastSeenName)
            {
                _settings.SetClientSetting(SettingsKeys.LastSeenName, _clientStateSingleton.LastSeenName);
            }

            //save window position
            base.OnClosing(e);

            //stop timer
            _updateTimer?.Stop();

            // Stop UI redraw timer
            _redrawUITimer?.Stop();

            Stop();

            _audioPreview?.StopEncoding();
            _audioPreview = null;

            _radioOverlayWindow?.Close();
            _radioOverlayWindow = null;

            _awacsRadioOverlay?.Close();
            _awacsRadioOverlay = null;
        }

        protected override void OnStateChanged(EventArgs e)
        {
            if (WindowState == WindowState.Minimized && _settings.GetClientSetting(SettingsKeys.MinimiseToTray).BoolValue)
            {
                Hide();
            }

            base.OnStateChanged(e);
        }

        private void UpdateUICallback()
        {
            if (_clientStateSingleton.IsConnected)
            {
                ToggleServerSettings.IsEnabled = true;

                bool eamEnabled = _serverSettings.GetSettingAsBool(Common.Setting.ServerSettingsKeys.EXTERNAL_AWACS_MODE);

                ExternalAWACSModePassword.IsEnabled = eamEnabled && !_clientStateSingleton.InExternalAWACSMode && !_clientStateSingleton.IsGameConnected;
                ExternalAWACSModePasswordLabel.IsEnabled = eamEnabled;
                ExternalAWACSModeName.IsEnabled = eamEnabled && !_clientStateSingleton.InExternalAWACSMode && !_clientStateSingleton.IsGameConnected;
                ExternalAWACSModeNameLabel.IsEnabled = eamEnabled;
                ConnectExternalAWACSMode.IsEnabled = eamEnabled;
                ConnectExternalAWACSMode.Content = _clientStateSingleton.InExternalAWACSMode ? "Disconnect External AWACS MODE (EAM)" : "Connect External AWACS MODE (EAM)";
            }
            else
            {
                ToggleServerSettings.IsEnabled = false;
                ExternalAWACSModePassword.IsEnabled = false;
                ExternalAWACSModePasswordLabel.IsEnabled = false;
                ExternalAWACSModeName.IsEnabled = false;
                ExternalAWACSModeNameLabel.IsEnabled = false;
                ConnectExternalAWACSMode.IsEnabled = false;
                ConnectExternalAWACSMode.Content = "Connect External AWACS MODE (EAM)";
            }
        }

        private void RadioEncryptionEffects_Click(object sender, RoutedEventArgs e)
        {
            _settings.SetClientSetting(SettingsKeys.RadioEncryptionEffects,
                (string) RadioEncryptionEffectsToggle.Content);
        }

        private void RadioSwitchPTT_Click(object sender, RoutedEventArgs e)
        {
            _settings.SetClientSetting(SettingsKeys.RadioSwitchIsPTT, (string) RadioSwitchIsPTT.Content);
        }

        private void ShowOverlay_OnClick(object sender, RoutedEventArgs e)
        {
            ToggleOverlay(true);
        }

        private void ToggleOverlay(bool uiButton)
        {
            //debounce show hide (1 tick = 100ns, 6000000 ticks = 600ms debounce)
            if ((DateTime.Now.Ticks - _toggleShowHide > 6000000) || uiButton)
            {
                _toggleShowHide = DateTime.Now.Ticks;
                if ((_radioOverlayWindow == null) || !_radioOverlayWindow.IsVisible ||
                    (_radioOverlayWindow.WindowState == WindowState.Minimized))
                {
                    //hide awacs panel
                    _awacsRadioOverlay?.Close();
                    _awacsRadioOverlay = null;

                    _radioOverlayWindow?.Close();

                    _radioOverlayWindow = new Overlay.RadioOverlayWindow();


                    _radioOverlayWindow.ShowInTaskbar =
                        !_settings.GetClientSetting(SettingsKeys.RadioOverlayTaskbarHide).BoolValue;
                    _radioOverlayWindow.Show();
                }
                else
                {
                    _radioOverlayWindow?.Close();
                    _radioOverlayWindow = null;
                }
            }
        }

        private void ShowAwacsOverlay_OnClick(object sender, RoutedEventArgs e)
        {
            if ((_awacsRadioOverlay == null) || !_awacsRadioOverlay.IsVisible ||
                (_awacsRadioOverlay.WindowState == WindowState.Minimized))
            {
                //close normal overlay
                _radioOverlayWindow?.Close();
                _radioOverlayWindow = null;

                _awacsRadioOverlay?.Close();

                _awacsRadioOverlay = new AwacsRadioOverlayWindow.RadioOverlayWindow();
                _awacsRadioOverlay.ShowInTaskbar =
                    !_settings.GetClientSetting(SettingsKeys.RadioOverlayTaskbarHide).BoolValue;
                _awacsRadioOverlay.Show();
            }
            else
            {
                _awacsRadioOverlay?.Close();
                _awacsRadioOverlay = null;
            }
        }

        private void ResetRadioWindow_Click(object sender, RoutedEventArgs e)
        {
            //close overlay
            _radioOverlayWindow?.Close();
            _radioOverlayWindow = null;


            _settings.GetPositionSetting(SettingsKeys.RadioX).DoubleValue = 300;
            _settings.GetPositionSetting(SettingsKeys.RadioY).DoubleValue = 300;

            _settings.GetPositionSetting(SettingsKeys.RadioWidth).DoubleValue = 122;
            _settings.GetPositionSetting(SettingsKeys.RadioHeight).DoubleValue = 270;

            _settings.GetPositionSetting(SettingsKeys.RadioOpacity).DoubleValue = 1.0;

            _settings.Save();
        }

        private void ToggleServerSettings_OnClick(object sender, RoutedEventArgs e)
        {
            if ((_serverSettingsWindow == null) || !_serverSettingsWindow.IsVisible ||
                (_serverSettingsWindow.WindowState == WindowState.Minimized))
            {
                _serverSettingsWindow?.Close();

                _serverSettingsWindow = new ServerSettingsWindow();
                _serverSettingsWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                _serverSettingsWindow.Owner = this;
                _serverSettingsWindow.ShowDialog();
            }
            else
            {
                _serverSettingsWindow?.Close();
                _serverSettingsWindow = null;
            }
        }

        private void AutoConnectPromptToggle_Click(object sender, RoutedEventArgs e)
        {
            _settings.GetClientSetting(SettingsKeys.AutoConnectPrompt).BoolValue =
                (bool) AutoConnectPromptToggle.IsChecked;
            _settings.Save();
        }

        private void AutoConnectMismatchPromptToggle_Click(object sender, RoutedEventArgs e)
        {
            _settings.GetClientSetting(SettingsKeys.AutoConnectMismatchPrompt).BoolValue =
                (bool)AutoConnectMismatchPromptToggle.IsChecked;
            _settings.Save();
        }

        private void RadioOverlayTaskbarItem_Click(object sender, RoutedEventArgs e)
        {
            _settings.GetClientSetting(SettingsKeys.RadioOverlayTaskbarHide).BoolValue =
                (bool) RadioOverlayTaskbarItem.IsChecked;
            _settings.Save();

            if (_radioOverlayWindow != null)
                _radioOverlayWindow.ShowInTaskbar = !_settings.GetClientSetting(SettingsKeys.RadioOverlayTaskbarHide).BoolValue;
            else if (_awacsRadioOverlay != null) _awacsRadioOverlay.ShowInTaskbar = !_settings.GetClientSetting(SettingsKeys.RadioOverlayTaskbarHide).BoolValue;
        }

        private void DCSRefocus_OnClick_Click(object sender, RoutedEventArgs e)
        {
            _settings.GetClientSetting(SettingsKeys.RefocusDCS).BoolValue =
                (bool) RefocusDCS.IsChecked;
            _settings.Save();
        }

        private void ExpandInputDevices_OnClick_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "You must restart SRS for this setting to take effect.\n\nTurning this on will allow almost any DirectX device to be used as input expect a Mouse but may cause issues with other devices being detected",
                "Restart SimpleRadio Standalone", MessageBoxButton.OK,
                MessageBoxImage.Warning);

            _settings.GetClientSetting(SettingsKeys.ExpandControls).BoolValue =
                (bool) ExpandInputDevices.IsChecked;
            _settings.Save();
        }

        private void LaunchAddressTab(object sender, RoutedEventArgs e)
        {
            TabControl.SelectedItem = FavouritesSeversTab;
        }

        private void RadioSoundEffects_OnClick(object sender, RoutedEventArgs e)
        {
            _settings.GetClientSetting(SettingsKeys.RadioEffects).BoolValue =
                (bool) RadioSoundEffects.IsChecked;
            _settings.Save();
        }

        private void RadioTxStart_Click(object sender, RoutedEventArgs e)
        {
            _settings.GetClientSetting(SettingsKeys.RadioTxEffects_Start).BoolValue =
                (bool) RadioTxStartToggle.IsChecked;
            _settings.Save();
        }

        private void RadioTxEnd_Click(object sender, RoutedEventArgs e)
        {
            _settings.GetClientSetting(SettingsKeys.RadioTxEffects_End).BoolValue =
                (bool) RadioTxEndToggle.IsChecked;
            _settings.Save();
        }

        private void RadioRxStart_Click(object sender, RoutedEventArgs e)
        {
            _settings.GetClientSetting(SettingsKeys.RadioRxEffects_Start).BoolValue =
                (bool) RadioRxStartToggle.IsChecked;
            _settings.Save();
        }

        private void RadioRxEnd_Click(object sender, RoutedEventArgs e)
        {
            _settings.GetClientSetting(SettingsKeys.RadioRxEffects_End).BoolValue =
                (bool) RadioRxEndToggle.IsChecked;
            _settings.Save();
        }

        private void AudioSelectChannel_OnClick(object sender, RoutedEventArgs e)
        {
            _settings.GetClientSetting(SettingsKeys.AutoSelectPresetChannel).BoolValue =
                (bool) AutoSelectChannel.IsChecked;
            _settings.Save();
        }

        private void RadioSoundEffectsClipping_OnClick(object sender, RoutedEventArgs e)
        {
            _settings.GetClientSetting(SettingsKeys.RadioEffectsClipping).BoolValue =
                (bool)RadioSoundEffectsClipping.IsChecked;
            _settings.Save();

        }

        private void MinimiseToTray_OnClick(object sender, RoutedEventArgs e)
        {
            _settings.GetClientSetting(SettingsKeys.MinimiseToTray).BoolValue =
                (bool)MinimiseToTray.IsChecked;
            _settings.Save();
        }

        private void StartMinimised_OnClick(object sender, RoutedEventArgs e)
        {
            _settings.GetClientSetting(SettingsKeys.StartMinimised).BoolValue =
                (bool)StartMinimised.IsChecked;
            _settings.Save();
        }

        private void AllowDCSPTT_OnClick(object sender, RoutedEventArgs e)
        {
            _settings.GetClientSetting(SettingsKeys.AllowDCSPTT).BoolValue =
                (bool)AllowDCSPTT.IsChecked;
            _settings.Save();
        }

        private void AlwaysAllowHotas_OnClick(object sender, RoutedEventArgs e)
        {
            _settings.GetClientSetting(SettingsKeys.AlwaysAllowHotasControls).BoolValue =
                (bool)AlwaysAllowHotas.IsChecked;
            _settings.Save();

        }

        private void CheckForBetaUpdates_OnClick(object sender, RoutedEventArgs e)
        {
            _settings.GetClientSetting(SettingsKeys.CheckForBetaUpdates).BoolValue =
                (bool)CheckForBetaUpdates.IsChecked;
            _settings.Save();
        }

        private void PlayConnectionSounds_OnClick(object sender, RoutedEventArgs e)
        {
            _settings.GetClientSetting(SettingsKeys.PlayConnectionSounds).BoolValue =
                (bool)PlayConnectionSounds.IsChecked;
            _settings.Save();
        }

        private void ConnectExternalAWACSMode_OnClick(object sender, RoutedEventArgs e)
        {
            ConnectExternalAwacsMode();
        }

        private void ConnectExternalAwacsMode()
        {
            if (_client == null ||
                !_clientStateSingleton.IsConnected ||
                !_serverSettings.GetSettingAsBool(Common.Setting.ServerSettingsKeys.EXTERNAL_AWACS_MODE) ||
                (!_clientStateSingleton.InExternalAWACSMode &&
                string.IsNullOrWhiteSpace(ExternalAWACSModePassword.Password)))
            {
                return;
            }

            _clientStateSingleton.LastSeenName = ExternalAWACSModeName.Text;

            // Already connected, disconnect
            if (_clientStateSingleton.InExternalAWACSMode)
            {
                _client.DisconnectExternalAWACSMode();
            }
            else
            {
                _client.ConnectExternalAWACSMode(ExternalAWACSModePassword.Password.Trim(), ExternalAWACSModeConnectionChanged);
            }
        }

        private void ExternalAWACSModeConnectionChanged(bool result, int coalition)
        {
            if (result)
            {
                _clientStateSingleton.InExternalAWACSMode = true;
                _clientStateSingleton.DcsPlayerSideInfo.side = coalition;
                _clientStateSingleton.DcsPlayerSideInfo.name = _clientStateSingleton.LastSeenName;
                _clientStateSingleton.DcsPlayerRadioInfo.name = _clientStateSingleton.LastSeenName;

                ConnectExternalAWACSMode.Content = "Disconnect External AWACS MODE (EAM)";
                ExternalAWACSModePassword.IsEnabled = false;
                ExternalAWACSModeName.IsEnabled = false;
            }
            else
            {
                _clientStateSingleton.InExternalAWACSMode = false;
                _clientStateSingleton.DcsPlayerSideInfo.side = 0;
                _clientStateSingleton.DcsPlayerSideInfo.name = "";
                _clientStateSingleton.DcsPlayerRadioInfo.name = "";
                _clientStateSingleton.DcsPlayerRadioInfo.LastUpdate = 0;
                _clientStateSingleton.LastSent = 0;

                ConnectExternalAWACSMode.Content = "Connect External AWACS MODE (EAM)";
                ExternalAWACSModePassword.IsEnabled = _serverSettings.GetSettingAsBool(Common.Setting.ServerSettingsKeys.EXTERNAL_AWACS_MODE);
                ExternalAWACSModeName.IsEnabled = _serverSettings.GetSettingAsBool(Common.Setting.ServerSettingsKeys.EXTERNAL_AWACS_MODE);
            }
        }

        private void RescanInputDevices(object sender, RoutedEventArgs e)
        {
            InputManager.InitDevices();
            MessageBox.Show(this,
                "Input Devices Rescanned",
                "New input devices can now be used.",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }
}