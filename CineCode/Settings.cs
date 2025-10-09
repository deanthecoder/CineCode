// Code authored by Dean Edis (DeanTheCoder).
// Anyone is free to copy, modify, use, compile, or distribute this software,
// either in source code form or as a compiled binary, for any non-commercial
// purpose.
// 
// If you modify the code, please retain this copyright header,
// and consider contributing back to the repository or letting us know
// about your modifications. Your contributions are valued!
// 
// THE SOFTWARE IS PROVIDED AS IS, WITHOUT WARRANTY OF ANY KIND.
using System.Collections.Generic;
using DTC.Core.Settings;

namespace CineCode;

public class Settings : UserSettingsBase
{
    internal const string DefaultYouTubeVideoId = "PLyPEwZQPST3okfxneqOAKsz2kYVMbLWlI";
    public static Settings Instance { get; } = new Settings();

    public List<string> MruFiles
    {
        get => Get<List<string>>();
        set => Set(value);
    }

    public List<string> RecentYouTubeItems
    {
        get
        {
            var value = Get<List<string>>();
            if (value is null)
            {
                value = [];
                Set(value);
            }
            return value;
        }
        set => Set(value);
    }

    public double Opacity
    {
        get => Get<double>();
        set => Set(value);
    }

    public double Volume
    {
        get => Get<double>();
        set => Set(value);
    }

    public string YouTubeVideoId
    {
        get => Get<string>();
        set => Set(value);
    }
    
    protected override void ApplyDefaults()
    {
        MruFiles = [];
        RecentYouTubeItems = [];
        Opacity = 0.85;
        Volume = 0.5;
        YouTubeVideoId = DefaultYouTubeVideoId;
    }
}
