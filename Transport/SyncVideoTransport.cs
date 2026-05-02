using BepInEx.Logging;
using BombRushMP.Common.Networking;
using BombRushMP.Plugin;
using SyncVideo.Transport.Packets;
using System;

namespace SyncVideo.Transport
{
    public sealed class SyncVideoTransport : IDisposable
    {
        private readonly ManualLogSource _logger;
        private bool _disposed;

        public event Action<SyncVideoPacketBase> SyncPacketReceived;

        public SyncVideoTransport(ManualLogSource logger)
        {
            _logger = logger;

            // Custom packet handlers register once and persist
            ClientController.RegisterCustomPacketHandler(SyncVideoPacketIds.State,          OnRawState);
            ClientController.RegisterCustomPacketHandler(SyncVideoPacketIds.Time,           OnRawTime);
            ClientController.RegisterCustomPacketHandler(SyncVideoPacketIds.StateRequest,   OnRawStateRequest);
            ClientController.RegisterCustomPacketHandler(SyncVideoPacketIds.LobbyAdvertise, OnRawLobbyAdvertise);
            ClientController.RegisterCustomPacketHandler(SyncVideoPacketIds.LobbyJoin,      OnRawLobbyJoin);
            ClientController.RegisterCustomPacketHandler(SyncVideoPacketIds.LobbyLeave,     OnRawLobbyLeave);
            ClientController.RegisterCustomPacketHandler(SyncVideoPacketIds.LobbyMembers,   OnRawLobbyMembers);
            ClientController.RegisterCustomPacketHandler(SyncVideoPacketIds.LobbyClosed,    OnRawLobbyClosed);
            ClientController.RegisterCustomPacketHandler(SyncVideoPacketIds.ScreenTransform,OnRawScreenTransform);
            ClientController.RegisterCustomPacketHandler(SyncVideoPacketIds.Suggestion,     OnRawSuggestion);
            ClientController.RegisterCustomPacketHandler(SyncVideoPacketIds.SuggestionsOpen,OnRawSuggestionsOpen);
            ClientController.RegisterCustomPacketHandler(SyncVideoPacketIds.SuggestionAck,  OnRawSuggestionAck);
        }

        public void Dispose()
        {
            _disposed = true;
        }

        public ushort LocalPlayerId => ClientController.Instance != null ? ClientController.Instance.LocalID : (ushort)0;

        public bool Connected => ClientController.Instance != null && ClientController.Instance.Connected;

        public void BroadcastToLobby(SyncVideoPacketBase packet)
        {
            if (_disposed || !Connected || packet == null)
                return;

            try
            {
                ClientController.Instance.BroadcastCustomPacketToCurrentLobby(
                    packet.Serialize(),
                    packet.PacketId,
                    IMessage.SendModes.ReliableUnordered);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[SyncVideo] BroadcastToLobby(" + packet.PacketId + ") failed: " + ex.Message);
            }
        }

        // Send packet directly to a player, like if they arrive to the lobby late and a video is already playin
        public void SendToPlayer(SyncVideoPacketBase packet, ushort targetPlayerId)
        {
            if (_disposed || !Connected || packet == null || targetPlayerId == 0)
                return;

            try
            {
                ClientController.Instance.SendCustomPacketToPlayer(
                    packet.Serialize(),
                    packet.PacketId,
                    targetPlayerId,
                    IMessage.SendModes.ReliableUnordered);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[SyncVideo] SendToPlayer(" + packet.PacketId + " → " + targetPlayerId + ") failed: " + ex.Message);
            }
        }

        // Raw handlers

        private void Dispatch(SyncVideoPacketBase packet)
        {
            if (_disposed || packet == null)
                return;

            SyncPacketReceived?.Invoke(packet);
        }

        private void OnRawState(ushort fromId, byte[] data)
        {
            try { Dispatch(SyncVideoStatePacket.Deserialize(fromId, data)); }
            catch (Exception ex) { _logger.LogWarning("[SyncVideo] Deserialise State failed: " + ex.Message); }
        }

        private void OnRawTime(ushort fromId, byte[] data)
        {
            try { Dispatch(SyncVideoTimePacket.Deserialize(fromId, data)); }
            catch (Exception ex) { _logger.LogWarning("[SyncVideo] Deserialise Time failed: " + ex.Message); }
        }

        private void OnRawStateRequest(ushort fromId, byte[] data)
        {
            try { Dispatch(SyncVideoStateRequestPacket.Deserialize(fromId, data)); }
            catch (Exception ex) { _logger.LogWarning("[SyncVideo] Deserialise StateRequest failed: " + ex.Message); }
        }

        private void OnRawLobbyAdvertise(ushort fromId, byte[] data)
        {
            try { Dispatch(SyncVideoLobbyAdvertisePacket.Deserialize(fromId, data)); }
            catch (Exception ex) { _logger.LogWarning("[SyncVideo] Deserialise LobbyAdvertise failed: " + ex.Message); }
        }

        private void OnRawLobbyJoin(ushort fromId, byte[] data)
        {
            try { Dispatch(SyncVideoLobbyJoinPacket.Deserialize(fromId, data)); }
            catch (Exception ex) { _logger.LogWarning("[SyncVideo] Deserialise LobbyJoin failed: " + ex.Message); }
        }

        private void OnRawLobbyLeave(ushort fromId, byte[] data)
        {
            try { Dispatch(SyncVideoLobbyLeavePacket.Deserialize(fromId, data)); }
            catch (Exception ex) { _logger.LogWarning("[SyncVideo] Deserialise LobbyLeave failed: " + ex.Message); }
        }

        private void OnRawLobbyMembers(ushort fromId, byte[] data)
        {
            try { Dispatch(SyncVideoLobbyMembersPacket.Deserialize(fromId, data)); }
            catch (Exception ex) { _logger.LogWarning("[SyncVideo] Deserialise LobbyMembers failed: " + ex.Message); }
        }

        private void OnRawLobbyClosed(ushort fromId, byte[] data)
        {
            try { Dispatch(SyncVideoLobbyClosedPacket.Deserialize(fromId, data)); }
            catch (Exception ex) { _logger.LogWarning("[SyncVideo] Deserialise LobbyClosed failed: " + ex.Message); }
        }

        private void OnRawScreenTransform(ushort fromId, byte[] data)
        {
            try { Dispatch(SyncVideoScreenTransformPacket.Deserialize(fromId, data)); }
            catch (Exception ex) { _logger.LogWarning("[SyncVideo] Deserialise ScreenTransform failed: " + ex.Message); }
        }

        private void OnRawSuggestion(ushort fromId, byte[] data)
        {
            try { Dispatch(SyncVideoSuggestionPacket.Deserialize(fromId, data)); }
            catch (Exception ex) { _logger.LogWarning("[SyncVideo] Deserialise Suggestion failed: " + ex.Message); }
        }

        private void OnRawSuggestionsOpen(ushort fromId, byte[] data)
        {
            try { Dispatch(SyncVideoSuggestionsOpenPacket.Deserialize(fromId, data)); }
            catch (Exception ex) { _logger.LogWarning("[SyncVideo] Deserialise SuggestionsOpen failed: " + ex.Message); }
        }

        private void OnRawSuggestionAck(ushort fromId, byte[] data)
        {
            try { Dispatch(SyncVideoSuggestionAckPacket.Deserialize(fromId, data)); }
            catch (Exception ex) { _logger.LogWarning("[SyncVideo] Deserialise SuggestionAck failed: " + ex.Message); }
        }
    }
}
