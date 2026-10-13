using System;

namespace CloCloWidget;

// A conservative, managed microphone-only filter. The high-pass stage removes
// low rumble, while the soft gate attenuates quiet gaps between utterances.
// Neither stage can separate overlapping voices from the speaker's voice.
// quietGain is the level quiet gaps are reduced to; Arabic dictation uses a
// gentler value because breathy sounds such as ح, ه, ف, and ث are quiet.
internal sealed class MicrophoneNoiseFilter(int sampleRate, float quietGain = 0.35f)
{
    private const int FrameSamples = 160; // 10 ms at 16 kHz
    // Keep the gate open briefly after speech so soft word endings (a final
    // ه or ح, an English "th" or "f") are not attenuated with the gap.
    private const int HoldFrames = 15;
    private int _holdFrames;
    private readonly float _highPassPole = MathF.Exp(-2f * MathF.PI * 90f / sampleRate);
    private float _previousInput;
    private float _previousOutput;
    private float _noiseFloor = 0.003f;
    private float _gain = 1f;

    public void Process(float[] samples)
    {
        if (samples.Length == 0) return;
        var frame = new float[FrameSamples];
        for (var offset = 0; offset < samples.Length; offset += FrameSamples)
        {
            var count = Math.Min(FrameSamples, samples.Length - offset);
            double energy = 0;
            for (var i = 0; i < count; i++)
            {
                var input = samples[offset + i];
                var filtered = _highPassPole * (_previousOutput + input - _previousInput);
                _previousInput = input;
                _previousOutput = filtered;
                frame[i] = filtered;
                energy += filtered * filtered;
            }

            var rms = (float)Math.Sqrt(energy / count);
            // Learn the floor from quiet frames. A slow upward rate prevents
            // sustained speech from being learned as noise.
            if (rms < _noiseFloor * 1.5f)
                _noiseFloor = 0.95f * _noiseFloor + 0.05f * rms;
            else
                _noiseFloor = 0.999f * _noiseFloor + 0.001f * rms;
            _noiseFloor = Math.Clamp(_noiseFloor, 0.001f, 0.03f);

            if (rms >= _noiseFloor * 1.7f) _holdFrames = HoldFrames;
            else if (_holdFrames > 0) _holdFrames--;
            var target = _holdFrames > 0 ? 1f : quietGain;
            // Smooth both transitions to preserve consonant onsets and avoid
            // the hard cuts of a traditional noise gate.
            var step = (target - _gain) / count;
            for (var i = 0; i < count; i++)
            {
                _gain += step;
                samples[offset + i] = frame[i] * _gain;
            }
        }
    }
}
