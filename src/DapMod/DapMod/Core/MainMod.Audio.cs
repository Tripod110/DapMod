using System;
using System.IO;
using System.Text;
using MelonLoader;
using UnityEngine;

namespace DapMod.Core;

public partial class MainMod
{
    private void WarmAudioCacheIfNeeded()
    {
        if (!EnableRuntimeAudio || _audioWarmupComplete || Time.time < AudioWarmupDelay)
        {
            return;
        }

        EnsureAudioSource();
        LoadAudioClip(PerfectAudioFileName, warnIfMissing: false);
        LoadAudioClip(GoodAudioFileName, warnIfMissing: false);
        LoadAudioClip(BadAudioFileName, warnIfMissing: false);
        _audioWarmupComplete = true;
    }

    private void TryPlayDapAudioCue(string fileName)
    {
        if (!EnableRuntimeAudio)
        {
            return;
        }

        EnsureAudioSource();
        AudioClip? clip = LoadAudioClip(fileName, warnIfMissing: true);
        if (clip == null || _audioSource == null)
        {
            return;
        }

        try
        {
            _audioSource.PlayOneShot(clip);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"Could not play dap audio cue '{fileName}': {ex.Message}");
        }
    }

    private void EnsureAudioSource()
    {
        if (_audioSource != null)
        {
            return;
        }

        if (_audioHostObject == null)
        {
            _audioHostObject = new GameObject("DapModAudio");
            _audioHostObject.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(_audioHostObject);
        }

        _audioSource = _audioHostObject.GetComponent<AudioSource>();
        if (_audioSource == null)
        {
            _audioSource = _audioHostObject.AddComponent<AudioSource>();
        }

        _audioSource.playOnAwake = false;
        _audioSource.loop = false;
        _audioSource.spatialBlend = 0f;
        _audioSource.volume = 0.88f;
    }

    private AudioClip? LoadAudioClip(string fileName, bool warnIfMissing)
    {
        string fullPath = GetAudioCuePath(fileName);
        if (_audioClipCache.TryGetValue(fullPath, out AudioClip? cachedClip) && cachedClip != null)
        {
            return cachedClip;
        }

        try
        {
            if (!File.Exists(fullPath))
            {
                if (warnIfMissing && _missingAudioCueWarningsShown.Add(fullPath))
                {
                    MelonLogger.Warning($"Missing dap audio cue placeholder: {fullPath}");
                }

                return null;
            }

            AudioClip clip = LoadWavClip(fullPath);
            _audioClipCache[fullPath] = clip;
            return clip;
        }
        catch (Exception ex)
        {
            if (_missingAudioCueWarningsShown.Add(fullPath))
            {
                MelonLogger.Warning($"Could not load dap audio cue '{fullPath}': {ex.Message}");
            }

            return null;
        }
    }

    private static AudioClip LoadWavClip(string fullPath)
    {
        byte[] bytes = File.ReadAllBytes(fullPath);
        if (!TryDecodeWav(bytes, out float[] samples, out int channels, out int sampleRate))
        {
            throw new InvalidDataException("Only PCM/float WAV files are supported.");
        }

        int sampleCount = samples.Length / channels;
        AudioClip clip = AudioClip.Create(Path.GetFileNameWithoutExtension(fullPath), sampleCount, channels, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    private static bool TryDecodeWav(byte[] bytes, out float[] samples, out int channels, out int sampleRate)
    {
        samples = Array.Empty<float>();
        channels = 0;
        sampleRate = 0;

        if (bytes.Length < 44 ||
            Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" ||
            Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
        {
            return false;
        }

        short formatCode = 0;
        short bitsPerSample = 0;
        int dataOffset = -1;
        int dataSize = 0;
        int offset = 12;

        while (offset + 8 <= bytes.Length)
        {
            string chunkId = Encoding.ASCII.GetString(bytes, offset, 4);
            int chunkSize = BitConverter.ToInt32(bytes, offset + 4);
            int chunkDataOffset = offset + 8;

            if (chunkDataOffset > bytes.Length)
            {
                break;
            }

            int boundedChunkSize = Math.Min(chunkSize, bytes.Length - chunkDataOffset);

            switch (chunkId)
            {
                case "fmt " when boundedChunkSize >= 16:
                    formatCode = BitConverter.ToInt16(bytes, chunkDataOffset + 0);
                    channels = BitConverter.ToInt16(bytes, chunkDataOffset + 2);
                    sampleRate = BitConverter.ToInt32(bytes, chunkDataOffset + 4);
                    bitsPerSample = BitConverter.ToInt16(bytes, chunkDataOffset + 14);
                    break;

                case "data":
                    dataOffset = chunkDataOffset;
                    dataSize = boundedChunkSize;
                    break;
            }

            offset = chunkDataOffset + boundedChunkSize;
            if ((boundedChunkSize & 1) == 1)
            {
                offset++;
            }
        }

        if (channels <= 0 || sampleRate <= 0 || dataOffset < 0 || dataSize <= 0)
        {
            return false;
        }

        if (formatCode == 1 && bitsPerSample == 16)
        {
            int sampleTotal = dataSize / 2;
            samples = new float[sampleTotal];
            for (int i = 0; i < sampleTotal; i++)
            {
                short pcmValue = BitConverter.ToInt16(bytes, dataOffset + (i * 2));
                samples[i] = pcmValue / 32768f;
            }

            return true;
        }

        if (formatCode == 1 && bitsPerSample == 8)
        {
            int sampleTotal = dataSize;
            samples = new float[sampleTotal];
            for (int i = 0; i < sampleTotal; i++)
            {
                samples[i] = (bytes[dataOffset + i] - 128) / 128f;
            }

            return true;
        }

        if (formatCode == 3 && bitsPerSample == 32)
        {
            int sampleTotal = dataSize / 4;
            samples = new float[sampleTotal];
            Buffer.BlockCopy(bytes, dataOffset, samples, 0, dataSize);
            return true;
        }

        return false;
    }
}
