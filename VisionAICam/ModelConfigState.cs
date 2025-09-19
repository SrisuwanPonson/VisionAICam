using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using VisionAICam.Pages;

namespace VisionAICam
{
    public static class ModelPageState
    {
        public static Border? DatasetBlock { get; set; }
        public static Border? ModelBlock { get; set; }
        public static Border? TrainBlock { get; set; }

        public static int ModelBlockAddCount { get; set; } = 0;
        public static string PretrainFolderPath { get; set; } = string.Empty;
    }


}
