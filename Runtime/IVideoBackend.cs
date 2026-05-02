using System;

namespace SyncVideo.Runtime
{
    public interface IVideoBackend
    {
        bool IsPrepared { get; }
        bool IsPlaying { get; }
        double CurrentTimeSeconds { get; }
        object OutputTexture { get; }
        string StatusOverlayText { get; }

        void Load(string directPlayableUrl, string originalUrl, string videoId);
        void Play();
        void Pause();
        void Stop();
        void Seek(double seconds);
        void NudgeToward(double seconds, double driftSeconds);
        void Tick(float deltaTime);
    }
}
