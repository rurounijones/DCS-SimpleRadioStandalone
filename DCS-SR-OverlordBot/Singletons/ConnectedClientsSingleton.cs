﻿using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Settings;
using Ciribob.DCS.SimpleRadio.Standalone.Common;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Network;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Setting;

namespace Ciribob.DCS.SimpleRadio.Standalone.Client.Singletons
{
    public sealed class ConnectedClientsSingleton : INotifyPropertyChanged
    {
        private readonly ConcurrentDictionary<string, SRClient> _clients = new ConcurrentDictionary<string, SRClient>();
        private static volatile ConnectedClientsSingleton _instance;
        private static readonly object Lock = new object();
        private readonly string _guid = ClientStateSingleton.Instance.ShortGuid;
        private readonly SyncedServerSettings _serverSettings = SyncedServerSettings.Instance;

        public event PropertyChangedEventHandler PropertyChanged;

        private ConnectedClientsSingleton() { }

        public static ConnectedClientsSingleton Instance
        {
            get
            {
                if (_instance != null) return _instance;
                lock (Lock)
                {
                    if (_instance == null)
                        _instance = new ConnectedClientsSingleton();
                }

                return _instance;
            }
        }

        private void NotifyPropertyChanged(string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void NotifyAll()
        {
            NotifyPropertyChanged("Total");
            NotifyPropertyChanged("InGame");
        }

        public SRClient this[string key]
        {
            get => _clients[key];
            set
            {
                _clients[key] = value;
                NotifyAll();
            }
        }

        public ICollection<SRClient> Values => _clients.Values;

        public int Total => _clients.Count;

        public int InGame
        {
            get
            {
                return _clients.Values.Count(client => client.IsIngame());
            }
        }

        public bool TryRemove(string key, out SRClient value)
        {
            var result = _clients.TryRemove(key, out value);
            if (result)
            {
                NotifyAll();
            }
            return result;
        }

        public void Clear()
        {
            _clients.Clear();
            NotifyAll();
        }

        public bool TryGetValue(string key, out SRClient value)
        {
            return _clients.TryGetValue(key, out value);
        }

        public bool ContainsKey(string key)
        {
            return _clients.ContainsKey(key);
        }

        public List<SRClient> ClientsOnFreq(double freq, RadioInformation.Modulation modulation)
        {
            if (!_serverSettings.GetSettingAsBool(ServerSettingsKeys.SHOW_TUNED_COUNT))
            {
                return new List<SRClient>();
            }
            var currentClientPos = ClientStateSingleton.Instance.PlayerCoalitionLocationMetadata;
            var currentUnitId = ClientStateSingleton.Instance.DcsPlayerRadioInfo.unitId;
            var coalitionSecurity = SyncedServerSettings.Instance.GetSettingAsBool(ServerSettingsKeys.COALITION_AUDIO_SECURITY);
            var globalFrequencies = _serverSettings.GlobalFrequencies;
            var global = globalFrequencies.Contains(freq);

            return (from client in _clients
                where !client.Key.Equals(_guid)
                where global || !coalitionSecurity || client.Value.Coalition == currentClientPos.side
                let radioInfo = client.Value.RadioInfo 
                where radioInfo != null
                let receivingRadio = radioInfo.CanHearTransmission(freq, modulation, 0, currentUnitId, new List<int>(), out _, out _)
                where receivingRadio != null
                select client.Value).ToList();
        }
    }
}
