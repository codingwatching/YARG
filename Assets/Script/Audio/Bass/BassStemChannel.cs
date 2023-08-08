using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using ManagedBass;
using ManagedBass.Fx;
using ManagedBass.Mix;
using UnityEngine;

namespace YARG.Audio.BASS
{
    public class BassStemChannel : IStemChannel
    {
        private const EffectType REVERB_TYPE = EffectType.Freeverb;

        public SongStem Stem { get; }
        public double LengthD { get; private set; }

        public double Volume { get; private set; }

        public int StreamHandle { get; private set; }
        public int ReverbStreamHandle { get; private set; }

        public bool IsMixed { get; set; } = false;

        private int _channelEndHandle;
        private event Action _channelEnd;

        public event Action ChannelEnd
        {
            add
            {
                if (_channelEndHandle == 0)
                {
                    SyncProcedure sync = (_, _, _, _) =>
                    {
                        // Prevent potential race conditions by caching the value as a local
                        var end = _channelEnd;
                        if (end != null)
                        {
                            UnityMainThreadCallback.QueueEvent(end.Invoke);
                        }
                    };
                    _channelEndHandle = IsMixed
                        ? BassMix.ChannelSetSync(StreamHandle, SyncFlags.End, 0, sync)
                        : Bass.ChannelSetSync(StreamHandle, SyncFlags.End, 0, sync);
                }

                _channelEnd += value;
            }
            remove { _channelEnd -= value; }
        }

        private readonly string _path;
        private readonly IAudioManager _manager;

        private readonly Dictionary<EffectType, int> _effects;

        private double _lastStemVolume;

        private int _sourceHandle;
        private bool _sourceIsSplit;

        private int _pitchFxHandle;
        private int _pitchFxReverbHandle;

        private bool _isReverbing;
        private bool _disposed;

		private PitchShiftParametersStruct _pitchParams = new(1, 0, AudioOptions.WHAMMY_FFT_DEFAULT,
            AudioOptions.WHAMMY_OVERSAMPLE_DEFAULT);

        public BassStemChannel(IAudioManager manager, string path, SongStem stem)
        {
            _manager = manager;
            _path = path;
            Stem = stem;

            Volume = 1;

            _lastStemVolume = _manager.GetVolumeSetting(Stem);
            _effects = new Dictionary<EffectType, int>();
        }

        public BassStemChannel(IAudioManager manager, SongStem stem, int sourceStream, bool isSplit)
        {
            _manager = manager;
            _sourceHandle = sourceStream;
            _sourceIsSplit = isSplit;

            Stem = stem;
            Volume = 1;

            _lastStemVolume = _manager.GetVolumeSetting(Stem);
            _effects = new Dictionary<EffectType, int>();
        }

        ~BassStemChannel()
        {
            Dispose(false);
        }

        public int Load(float speed)
        {
            if (_disposed)
            {
                return -1;
            }

            if (StreamHandle != 0)
            {
                return 0;
            }

            if (_sourceHandle == 0)
            {
                if (string.IsNullOrEmpty(_path))
                {
                    // Channel was not set up correctly for some reason
                    return -1;
                }

                // Last flag is new BASS_SAMPLE_NOREORDER flag, which is not in the BassFlags enum,
                // as it was made as part of an update to fix <= 8 channel oggs.
                // https://www.un4seen.com/forum/?topic=20148.msg140872#msg140872
                const BassFlags flags = BassFlags.Prescan | BassFlags.Decode | BassFlags.AsyncFile | (BassFlags) 64;

                _sourceHandle = Bass.CreateStream(_path, 0, 0, flags);
                if (_sourceHandle == 0)
                {
                    return (int) Bass.LastError;
                }
            }

            int main = BassMix.CreateSplitStream(_sourceHandle, BassFlags.Decode | BassFlags.SplitPosition, null);
            int reverbSplit =
                BassMix.CreateSplitStream(_sourceHandle, BassFlags.Decode | BassFlags.SplitPosition, null);

            const BassFlags tempoFlags =
                BassFlags.SampleOverrideLowestVolume | BassFlags.Decode | BassFlags.FxFreeSource;

            StreamHandle = BassFx.TempoCreate(main, tempoFlags);
            ReverbStreamHandle = BassFx.TempoCreate(reverbSplit, tempoFlags);

            // Apply a compressor to balance stem volume
            Bass.ChannelSetFX(StreamHandle, EffectType.Compressor, 1);
            Bass.ChannelSetFX(ReverbStreamHandle, EffectType.Compressor, 1);

            var compressorParams = new CompressorParameters
            {
                fGain = -3,
                fThreshold = -2,
                fAttack = 0.01f,
                fRelease = 0.1f,
                fRatio = 4,
            };

            Bass.FXSetParameters(StreamHandle, compressorParams);
            Bass.FXSetParameters(ReverbStreamHandle, compressorParams);

            Bass.ChannelSetAttribute(StreamHandle, ChannelAttribute.Volume, _manager.GetVolumeSetting(Stem));
            Bass.ChannelSetAttribute(ReverbStreamHandle, ChannelAttribute.Volume, 0);

            if (_manager.Options.UseWhammyFx && AudioHelpers.PitchBendAllowedStems.Contains(Stem))
            {
                // Setting the FFT size causes a crash in BASS_FX :/
                // _pitchParams.FFTSize = _manager.Options.WhammyFFTSize;
                _pitchParams.OversampleFactor = _manager.Options.WhammyOversampleFactor;

                _pitchFxHandle = Bass.ChannelSetFX(StreamHandle, EffectType.PitchShift, 0);
                if (_pitchFxHandle == 0)
                {
                    Debug.LogError("Failed to add pitch shift (normal fx): " + Bass.LastError);
                }
                else if (!BassHelpers.FXSetParameters(_pitchFxHandle, _pitchParams))
                {
                    Debug.LogError("Failed to set pitch shift params (normal fx): " + Bass.LastError);
                    Bass.ChannelRemoveFX(StreamHandle, _pitchFxHandle);
                    _pitchFxHandle = 0;
                }

                _pitchFxReverbHandle = Bass.ChannelSetFX(ReverbStreamHandle, EffectType.PitchShift, 0);
                if (_pitchFxReverbHandle == 0)
                {
                    Debug.LogError("Failed to add pitch shift (reverb fx): " + Bass.LastError);
                }
                else if (!BassHelpers.FXSetParameters(_pitchFxReverbHandle, _pitchParams))
                {
                    Debug.LogError("Failed to set pitch shift params (reverb fx): " + Bass.LastError);
                    Bass.ChannelRemoveFX(ReverbStreamHandle, _pitchFxReverbHandle);
                    _pitchFxReverbHandle = 0;
                }

                // Set position to trigger the pitch bend delay compensation
                SetPosition(0);
            }

            if (!Mathf.Approximately(speed, 1f))
            {
                SetSpeed(speed);

                // Have to handle pitch separately for some reason
                if (_manager.Options.IsChipmunkSpeedup)
                {
                    float semitoneShift = speed switch
                    {
                        > 1 => speed / 9 - 1 / 9,
                        < 1 => speed / 3 - 1 / 3,
                        _     => 0
                    };

                    semitoneShift = Math.Clamp(semitoneShift, -60, 60);

                    Bass.ChannelSetAttribute(StreamHandle, ChannelAttribute.Pitch, semitoneShift);
                    Bass.ChannelSetAttribute(ReverbStreamHandle, ChannelAttribute.Pitch, semitoneShift);
                }
            }

            LengthD = GetLengthInSeconds();

            return 0;
        }

        public void FadeIn(float maxVolume)
        {
            Bass.ChannelSetAttribute(StreamHandle, ChannelAttribute.Volume, 0);
            Bass.ChannelSlideAttribute(StreamHandle, ChannelAttribute.Volume, maxVolume,
                BassHelpers.FADE_TIME_MILLISECONDS);
        }

        public UniTask FadeOut()
        {
            Bass.ChannelSlideAttribute(StreamHandle, ChannelAttribute.Volume, 0, BassHelpers.FADE_TIME_MILLISECONDS);
            return UniTask.WaitUntil(() =>
            {
                Bass.ChannelGetAttribute(StreamHandle, ChannelAttribute.Volume, out var currentVolume);
                return Mathf.Abs(currentVolume) <= 0.01f;
            });
        }

        public void SetVolume(double newVolume)
        {
            if (StreamHandle == 0)
            {
                return;
            }

            double volumeSetting = _manager.GetVolumeSetting(Stem);

            double oldBassVol = _lastStemVolume * Volume;
            double newBassVol = volumeSetting * newVolume;

            // Values are the same, no need to change
            if (Math.Abs(oldBassVol - newBassVol) < double.Epsilon)
            {
                return;
            }

            Volume = newVolume;
            _lastStemVolume = volumeSetting;

            Bass.ChannelSetAttribute(StreamHandle, ChannelAttribute.Volume, newBassVol);

            if (_isReverbing)
            {
                Bass.ChannelSlideAttribute(ReverbStreamHandle, ChannelAttribute.Volume, (float) (newBassVol * 0.7), 1);
            }
            else
            {
                Bass.ChannelSlideAttribute(ReverbStreamHandle, ChannelAttribute.Volume, 0, 1);
            }
        }

        public void SetReverb(bool reverb)
        {
            _isReverbing = reverb;
            if (reverb)
            {
                // Reverb already applied
                if (_effects.ContainsKey(REVERB_TYPE)) return;

                // Set reverb FX
                int lowEqHandle = BassHelpers.AddEqToChannel(ReverbStreamHandle, BassHelpers.LowEqParams);
                int midEqHandle = BassHelpers.AddEqToChannel(ReverbStreamHandle, BassHelpers.MidEqParams);
                int highEqHandle = BassHelpers.AddEqToChannel(ReverbStreamHandle, BassHelpers.HighEqParams);
                int reverbFxHandle = BassHelpers.AddReverbToChannel(ReverbStreamHandle);

                double volumeSetting = _manager.GetVolumeSetting(Stem);
                Bass.ChannelSlideAttribute(ReverbStreamHandle, ChannelAttribute.Volume,
                    (float) (volumeSetting * Volume * 0.7f),
                    BassHelpers.REVERB_SLIDE_IN_MILLISECONDS);

                _effects.Add(REVERB_TYPE, reverbFxHandle);

                // Add low-high
                _effects.Add(EffectType.PeakEQ, lowEqHandle);
                _effects.Add(EffectType.PeakEQ + 1, midEqHandle);
                _effects.Add(EffectType.PeakEQ + 2, highEqHandle);
            }
            else
            {
                // No reverb is applied
                if (!_effects.ContainsKey(REVERB_TYPE))
                {
                    return;
                }

                // Remove low-high
                Bass.ChannelRemoveFX(ReverbStreamHandle, _effects[EffectType.PeakEQ]);
                Bass.ChannelRemoveFX(ReverbStreamHandle, _effects[EffectType.PeakEQ + 1]);
                Bass.ChannelRemoveFX(ReverbStreamHandle, _effects[EffectType.PeakEQ + 2]);
                Bass.ChannelRemoveFX(ReverbStreamHandle, _effects[REVERB_TYPE]);

                Bass.ChannelSlideAttribute(ReverbStreamHandle, ChannelAttribute.Volume, 0,
                    BassHelpers.REVERB_SLIDE_OUT_MILLISECONDS);

                _effects.Remove(REVERB_TYPE);

                // Remove low-high
                _effects.Remove(EffectType.PeakEQ);
                _effects.Remove(EffectType.PeakEQ + 1);
                _effects.Remove(EffectType.PeakEQ + 2);
            }
        }

        public void SetSpeed(float speed)
        {
            speed = (float)Math.Round(Math.Clamp(speed, 0.05, 50), 2);

            // Gets relative speed from 100% (so 1.05f = 5% increase)
            float percentageSpeed = speed * 100;
            float relativeSpeed = percentageSpeed - 100;

            Bass.ChannelSetAttribute(StreamHandle, ChannelAttribute.Tempo, relativeSpeed);
            Bass.ChannelSetAttribute(ReverbStreamHandle, ChannelAttribute.Tempo, relativeSpeed);
        }

        public void SetWhammyPitch(float percent)
        {
            if (_pitchFxHandle == 0 || _pitchFxReverbHandle == 0)
                return;

            percent = Mathf.Clamp(percent, 0f, 1f);

            float shift = Mathf.Pow(2, -(_manager.Options.WhammyPitchShiftAmount * percent) / 12);
            _pitchParams.fPitchShift = shift;

            if (!BassHelpers.FXSetParameters(_pitchFxHandle, _pitchParams))
            {
                Debug.LogError("Failed to set params (normal fx): " + Bass.LastError);
            }

            if (!BassHelpers.FXSetParameters(_pitchFxReverbHandle, _pitchParams))
            {
                Debug.LogError("Failed to set params (reverb fx): " + Bass.LastError);
            }
        }

        private double GetDesyncOffset()
        {
            double desync = BassHelpers.PLAYBACK_BUFFER_DESYNC;

            // Hack to get desync of pitch-bent channels
            if (_pitchFxHandle != 0 && _pitchFxReverbHandle != 0)
            {
                // The desync is caused by the FFT window
                // BASS_FX does not account for it automatically so we must do it ourselves
                // (thanks Matt/Oscar for the info!)
                double sampleRate = Bass.ChannelGetAttribute(StreamHandle, ChannelAttribute.Frequency);
                desync += _pitchParams.FFTSize / sampleRate;
            }

            return desync;
        }

        public double GetPosition(bool desyncCompensation = true)
        {
            double position = Bass.ChannelBytes2Seconds(StreamHandle, Bass.ChannelGetPosition(StreamHandle));
            if (desyncCompensation)
                position -= GetDesyncOffset();
            return position;
        }

        public void SetPosition(double position, bool desyncCompensation = true)
        {
            if (desyncCompensation)
                position += GetDesyncOffset();

            if (IsMixed)
            {
                BassMix.ChannelSetPosition(StreamHandle, Bass.ChannelSeconds2Bytes(StreamHandle, position));
            }
            else
            {
                Bass.ChannelSetPosition(StreamHandle, Bass.ChannelSeconds2Bytes(StreamHandle, position));
            }

            if (_sourceIsSplit && !BassMix.SplitStreamReset(_sourceHandle))
                Debug.LogError($"Failed to reset stream: {Bass.LastError}");
        }

        public double GetLengthInSeconds()
        {
            return BassHelpers.GetChannelLengthInSeconds(StreamHandle);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                // Free managed resources here
                if (disposing)
                {
                }

                // Free unmanaged resources here
                if (StreamHandle != 0)
                {
                    Bass.StreamFree(StreamHandle);
                    StreamHandle = 0;
                }

                if (ReverbStreamHandle != 0)
                {
                    Bass.StreamFree(ReverbStreamHandle);
                    ReverbStreamHandle = 0;
                }

                if (_sourceHandle != 0)
                {
                    Bass.StreamFree(_sourceHandle);
                    _sourceHandle = 0;
                }

                _disposed = true;
            }
        }
    }
}