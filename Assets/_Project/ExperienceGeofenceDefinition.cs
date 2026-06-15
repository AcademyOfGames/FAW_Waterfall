using System;
using UnityEngine;

[Serializable]
public struct ExperienceGeofenceDefinition
{
    public string ExperienceName;
    public string SceneName;
    public double Latitude;
    public double Longitude;

    public static readonly ExperienceGeofenceDefinition[] All =
    {
        new ExperienceGeofenceDefinition
        {
            ExperienceName = "Benaroya",
            SceneName = "benaroyaScene",
            Latitude = 47.608,
            Longitude = -122.3362
        },
        new ExperienceGeofenceDefinition
        {
            ExperienceName = "Alina",
            SceneName = "AlinaScene",
            Latitude = 47.61974,
            Longitude = -122.3516
        },
        new ExperienceGeofenceDefinition
        {
            ExperienceName = "Divine",
            SceneName = "DivineScene",
            Latitude = 47.6111,
            Longitude = -122.339
        }
    };

    public const double EnterGeofenceKm = 0.1609344;

    /// <summary>Maps former Addressable labels to build-settings scene names.</summary>
    public static bool TryGetSceneNameForLegacyLabel(string label, out string sceneName)
    {
        if (string.IsNullOrEmpty(label))
        {
            sceneName = null;
            return false;
        }

        foreach (var def in All)
        {
            if (string.Equals(def.SceneName, label, StringComparison.Ordinal))
            {
                sceneName = def.SceneName;
                return true;
            }

            if (string.Equals(def.ExperienceName, label, StringComparison.OrdinalIgnoreCase))
            {
                sceneName = def.SceneName;
                return true;
            }
        }

        switch (label.ToLowerInvariant())
        {
            case "benaroya": sceneName = "benaroyaScene"; return true;
            case "alina": sceneName = "AlinaScene"; return true;
            case "divine": sceneName = "DivineScene"; return true;
            case "samplescene": sceneName = "DivineScene"; return true;
            case "dev": sceneName = "devScene"; return true;
            default:
                sceneName = null;
                return false;
        }
    }

    public static bool TryGetByExperienceName(string experienceName, out ExperienceGeofenceDefinition definition)
    {
        foreach (var def in All)
        {
            if (string.Equals(def.ExperienceName, experienceName, StringComparison.OrdinalIgnoreCase))
            {
                definition = def;
                return true;
            }
        }

        definition = default;
        return false;
    }

    public static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusKm = 6371.0;
        var dLat = (lat2 - lat1) * (Math.PI / 180.0);
        var dLon = (lon2 - lon1) * (Math.PI / 180.0);
        var a = Math.Sin(dLat / 2.0) * Math.Sin(dLat / 2.0) +
                Math.Cos(lat1 * (Math.PI / 180.0)) * Math.Cos(lat2 * (Math.PI / 180.0)) *
                Math.Sin(dLon / 2.0) * Math.Sin(dLon / 2.0);
        var c = 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1.0 - a));
        return earthRadiusKm * c;
    }
}
