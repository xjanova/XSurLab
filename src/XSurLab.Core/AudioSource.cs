namespace XSurLab.Core;

/// <summary>Interleaved samples (full scale ±1) for a monitor output. Read runs on one audio thread; the rest is safe from any thread.</summary>
public interface IAudioSource
{
    int SampleRate { get; }
    int Channels { get; }
    /// <summary>WAVE channel mask of the interleaved channels.</summary>
    uint ChannelMask { get; }
    /// <summary>A seek waits for the next Read; a player that queues audio should drop its queue when this is set.</summary>
    bool SeekPending { get; }
    /// <summary>Seeks applied by Read so far. A stage that keeps history clears it when this changes.</summary>
    int Seeks { get; }
    bool Finished { get; }
    /// <summary>Fills whole frames, clears the rest of the buffer and returns the frame count.</summary>
    int Read(Span<double> destination, CancellationToken cancellationToken = default);
}
