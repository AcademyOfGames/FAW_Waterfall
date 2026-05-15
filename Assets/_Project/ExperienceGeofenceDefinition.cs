using System;
using UnityEngine;

[Serializable]
public struct ExperienceGeofenceDefinition
{
    public string ExperienceName;
    public string SceneName;
    public string AddressableLabel;
    public double Latitude;
    public double Longitude;

    public static readonly ExperienceGeofenceDefinition[] All =
    {
        new ExperienceGeofenceDefinition
        {
            ExperienceName = "Benaroya",
            SceneName = "benaroyaScene",
            AddressableLabel = "benaroya",
            Latitude = 47.608,
            Longitude = -122.3362
        },
        new ExperienceGeofenceDefinition
        {
            ExperienceName = "Alina",
            SceneName = "AlinaScene",
            AddressableLabel = "alina",
            Latitude = 47.61974,
            Longitude = -122.3516
        },
        new ExperienceGeofenceDefinition
        {
            ExperienceName = "Divine",
            SceneName = "SampleScene",
            AddressableLabel = "divine",
            Latitude = 47.6111,
            Longitude = -122.339
        },
        new ExperienceGeofenceDefinition
        {
            ExperienceName = "Chenoa",
            SceneName = "ChenoaScene",
            AddressableLabel = "chenoa",
            Latitude = 47.599,
            Longitude = -122.3301
        },
        new ExperienceGeofenceDefinition
        {
            ExperienceName = "Dan",
            SceneName = "DanScene",
            AddressableLabel = "dan",
            Latitude = 47.6028,
            Longitude = -122.3312
        }
    };

    public const double EnterGeofenceKm = 0.1609344;

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
