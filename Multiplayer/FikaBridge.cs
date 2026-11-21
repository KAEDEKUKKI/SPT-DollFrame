#if HAS_FIKA
using System;
using EFT;
using Fika.Core.Main.Components;
using Fika.Core.Main.Players;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using Fika.Core.Networking.LiteNetLib.Utils;

namespace DollFrame.Multiplayer
{
    /// <summary>
    /// Fika 多人連線封包：同步玩家的 ProfileId 與選用的 DollFrame 角色名稱。
    /// CharacterName 為空字串代表 Vanilla (PMC)。
    /// </summary>
    public struct DollFrameAppearancePacket : INetSerializable
    {
        public string ProfileId;
        public string CharacterName;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(ProfileId ?? string.Empty);
            writer.Put(CharacterName ?? string.Empty);
        }

        public void Deserialize(NetDataReader reader)
        {
            ProfileId = reader.GetString();
            CharacterName = reader.GetString();
        }
    }

    /// <summary>
    /// Fika 多人橋接層（僅在 com.fika.core 載入時由 Plugin 延遲初始化，單機玩家零相依）。
    /// 負責：
    /// 1. 戰局開始／本機玩家生成／換角設定異動時，廣播本機角色外觀給所有隊友；
    /// 2. Host（FikaServer）接收 Client 封包後更新本地登錄表並轉發給其他 Client，
    ///    且在新連線（PeerConnectedEvent）時將所有已知玩家（含 Host 自身）外觀補發給新 Client；
    /// 3. 遠端 ObservedPlayer 生成時確保立即套用已同步的建模與語音。
    /// </summary>
    internal static class FikaBridge
    {
        private static bool _initialized;
        private static IFikaNetworkManager _networkManager;
        private static string _localProfileId;

        internal static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            FikaEventDispatcher.SubscribeEvent<FikaNetworkManagerCreatedEvent>(OnNetworkManagerCreated);
            FikaEventDispatcher.SubscribeEvent<FikaNetworkManagerDestroyedEvent>(OnNetworkManagerDestroyed);
            FikaEventDispatcher.SubscribeEvent<PeerConnectedEvent>(OnPeerConnected);
            FikaEventDispatcher.SubscribeEvent<GameWorldStartedEvent>(OnGameWorldStarted);
            FikaEventDispatcher.SubscribeEvent<FikaGameEndedEvent>(OnGameEnded);
            FikaPlayer.OnPlayerSpawned += OnFikaPlayerSpawned;

            Plugin.Log.LogInfo("[DollFrame] mp: Fika bridge initialized");
        }

        private static void OnNetworkManagerCreated(FikaNetworkManagerCreatedEvent e)
        {
            _networkManager = e.Manager;
            _localProfileId = ResolveLocalProfileId();

            if (_networkManager is FikaServer server)
            {
                server.RegisterPacket<DollFrameAppearancePacket, NetPeer>(OnServerPacketReceived);
            }
            else if (_networkManager != null)
            {
                _networkManager.RegisterPacket<DollFrameAppearancePacket>(OnClientPacketReceived);
            }
        }

        private static void OnNetworkManagerDestroyed(FikaNetworkManagerDestroyedEvent e)
        {
            _networkManager = null;
            _localProfileId = null;
            MultiplayerRegistry.Clear();
        }

        private static void OnGameEnded(FikaGameEndedEvent e)
        {
            _localProfileId = null;
            MultiplayerRegistry.Clear();
        }

        private static void OnPeerConnected(PeerConnectedEvent e)
        {
            if (!(_networkManager is FikaServer server) || e.Peer == null) return;

            var localId = ResolveLocalProfileId();
            if (!string.IsNullOrEmpty(localId))
            {
                var hostPkt = new DollFrameAppearancePacket
                {
                    ProfileId = localId,
                    CharacterName = MultiplayerRegistry.GetLocalBroadcastCharacter(),
                };
                server.SendDataToPeer(ref hostPkt, DeliveryMethod.ReliableOrdered, e.Peer);
            }

            foreach (var kv in MultiplayerRegistry.SnapshotAll())
            {
                if (string.IsNullOrEmpty(kv.Key) || string.Equals(kv.Key, localId, StringComparison.Ordinal))
                    continue;
                var pkt = new DollFrameAppearancePacket
                {
                    ProfileId = kv.Key,
                    CharacterName = kv.Value ?? string.Empty,
                };
                server.SendDataToPeer(ref pkt, DeliveryMethod.ReliableOrdered, e.Peer);
            }
        }

        private static void OnGameWorldStarted(GameWorldStartedEvent e)
        {
            BroadcastLocalAppearance();
        }

        private static void OnFikaPlayerSpawned(FikaPlayer player)
        {
            if (player == null) return;
            if (player.IsYourPlayer)
            {
                if (!string.IsNullOrEmpty(player.ProfileId))
                    _localProfileId = player.ProfileId;
                BroadcastLocalAppearance();
                return;
            }

            // 若封包在 ObservedPlayer 生成前就已抵達，生成後立即補套用建模與語音
            BodyReplacer.Apply(player);
            VoiceReplacer.Apply(player);
        }

        private static void OnServerPacketReceived(DollFrameAppearancePacket packet, NetPeer sender)
        {
            if (string.IsNullOrEmpty(packet.ProfileId)) return;
            MultiplayerRegistry.SetRemoteCharacter(packet.ProfileId, packet.CharacterName);

            if (_networkManager is FikaServer server && sender != null)
            {
                server.SendData(ref packet, DeliveryMethod.ReliableOrdered, sender, true);
            }
        }

        private static void OnClientPacketReceived(DollFrameAppearancePacket packet)
        {
            if (string.IsNullOrEmpty(packet.ProfileId)) return;
            MultiplayerRegistry.SetRemoteCharacter(packet.ProfileId, packet.CharacterName);
        }

        internal static void BroadcastLocalAppearance()
        {
            if (_networkManager == null) return;

            var profileId = ResolveLocalProfileId();
            if (string.IsNullOrEmpty(profileId)) return;

            var pkt = new DollFrameAppearancePacket
            {
                ProfileId = profileId,
                CharacterName = MultiplayerRegistry.GetLocalBroadcastCharacter(),
            };

            try
            {
                _networkManager.SendData(ref pkt, DeliveryMethod.ReliableOrdered, true);
                Plugin.Log.LogInfo($"[DollFrame] mp: broadcast local appearance '{(string.IsNullOrEmpty(pkt.CharacterName) ? "Vanilla (PMC)" : pkt.CharacterName)}' ({profileId})");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[DollFrame] mp: broadcast failed: {ex.Message}");
            }
        }

        private static string ResolveLocalProfileId()
        {
            if (!string.IsNullOrEmpty(_localProfileId)) return _localProfileId;

            if (CoopHandler.TryGetCoopHandler(out var coop) && coop.MyPlayer != null && !string.IsNullOrEmpty(coop.MyPlayer.ProfileId))
                return _localProfileId = coop.MyPlayer.ProfileId;

            var ownerPlayer = GamePlayerOwner.MyPlayer;
            if (ownerPlayer != null && !string.IsNullOrEmpty(ownerPlayer.ProfileId))
                return _localProfileId = ownerPlayer.ProfileId;

            return null;
        }
    }
}
#endif
