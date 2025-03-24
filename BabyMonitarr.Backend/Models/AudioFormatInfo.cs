using System;

namespace BabyMonitarr.Backend.Models
{
    public class AudioFormatInfo
    {
        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public int BitsPerSample { get; set; }
    }
} 