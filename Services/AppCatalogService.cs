using app_tự_động.Models;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace app_tự_động.Services
{
    public class AppCatalogService
    {
        public List<AppItem> LoadFromJson(string filePath)
        {
            if (!File.Exists(filePath))
                return new List<AppItem>();

            string json = File.ReadAllText(filePath);
            var apps = JsonConvert.DeserializeObject<List<AppItem>>(json);

            return apps ?? new List<AppItem>();
        }

        public void SaveToJson(string filePath, List<AppItem> apps)
        {
            string json = JsonConvert.SerializeObject(apps ?? new List<AppItem>(), Formatting.Indented);
            File.WriteAllText(filePath, json, new UTF8Encoding(true));
        }
    }
}