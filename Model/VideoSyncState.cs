using System;

namespace SyncVideo.Model
{
    public sealed class VideoSyncState
    {
        public string Url = string.Empty;
        public string VideoId = string.Empty;
        public bool IsPlaying;
        public double MediaTimeSeconds;
        public long HostUnixMilliseconds;
        public int Revision;
        public bool HasEnded;
        public bool IsOpen = true;

        public double GetExpectedPlaybackTimeUtc(DateTime utcNow)
        {
            if (!IsPlaying)
                return MediaTimeSeconds;

            var hostUtc = DateTimeOffset.FromUnixTimeMilliseconds(HostUnixMilliseconds).UtcDateTime;
            var elapsed = (utcNow - hostUtc).TotalSeconds;
            if (elapsed < 0d)
                elapsed = 0d;

            return MediaTimeSeconds + elapsed;
        }
    }
}
