using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VisionAICam_Linux
{
    public static class DatasetSplitter
    {
        public static (List<T> Train, List<T> Val, List<T> Test) Split<T>(
            List<T> items, double trainRatio, double valRatio, double testRatio)
        {
            if (Math.Abs(trainRatio + valRatio + testRatio - 1.0) > 0.001)
                throw new ArgumentException("Ratios must sum to 1.0");

            var rnd = new Random();
            var shuffled = items.OrderBy(_ => rnd.Next()).ToList();

            int total = shuffled.Count;
            int trainCount = (int)Math.Round(total * trainRatio);
            int valCount = (int)Math.Round(total * valRatio);

            var train = shuffled.Take(trainCount).ToList();
            var val = shuffled.Skip(trainCount).Take(valCount).ToList();
            var test = shuffled.Skip(trainCount + valCount).ToList();

            return (train, val, test);
        }
    }
}
