using System.Reflection;
using NAudio.Wave;

namespace Obr2SseApp;

/// Plays the embedded background track on a loop at a low volume, with a single mute toggle.
///
/// The mp3 is decoded to raw PCM in memory once at startup and the loop runs over that buffer, so the
/// wrap-around is seamless - restarting the mp3 decoder each loop leaves an audible gap.
public sealed class MusicPlayer : IDisposable
{
    private readonly WaveOutEvent _output = new();
    private readonly MemoryStream _pcm = new();
    private readonly RawSourceWaveStream _source;
    private readonly LoopStream _loop;
    private readonly float _volume;
    private bool _ready;

    public MusicPlayer(float volume)
    {
        _volume = volume;

        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase));

        WaveFormat format;
        using (var mp3 = new Mp3FileReader(assembly.GetManifestResourceStream(name)!))
        {
            format = mp3.WaveFormat;
            mp3.CopyTo(_pcm);
        }

        _pcm.Position = 0;
        _source = new RawSourceWaveStream(_pcm, format);
        _loop = new LoopStream(_source);

        try
        {
            _output.Init(_loop);
            _output.Volume = volume;
            _ready = true;
        }
        catch
        {
            // No usable audio device: run silent rather than crash.
            _ready = false;
        }
    }

    public bool Muted { get; private set; }

    public void Play()
    {
        if (!_ready)
            return;

        try
        {
            _output.Play();
        }
        catch
        {
            _ready = false;
        }
    }

    public void ToggleMute()
    {
        Muted = !Muted;

        if (!_ready)
            return;

        try
        {
            _output.Volume = Muted ? 0f : _volume;
        }
        catch
        {
            _ready = false;
        }
    }

    public void Dispose()
    {
        _output.Dispose();
        _loop.Dispose();
        _source.Dispose();
        _pcm.Dispose();
    }

    /// Restarts the stream at the end instead of stopping.
    private sealed class LoopStream(WaveStream source) : WaveStream
    {
        public override WaveFormat WaveFormat => source.WaveFormat;
        public override long Length => source.Length;

        public override long Position
        {
            get => source.Position;
            set => source.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = 0;

            while (read < count)
            {
                int n = source.Read(buffer, offset + read, count - read);
                if (n == 0)
                {
                    if (source.Position == 0)
                        break;

                    source.Position = 0;
                    continue;
                }

                read += n;
            }

            return read;
        }
    }
}
