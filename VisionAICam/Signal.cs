using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VisionAICam
{
    public class Signal<T>
    {
        private readonly List<Action<T>> _subscribers = new();

        public void Subscribe(Action<T> handler)
        {
            if (!_subscribers.Contains(handler))
                _subscribers.Add(handler);
        }

        public void Fire(T value)
        {
            foreach (var handler in _subscribers)
                handler.Invoke(value);
        }
    }


    public static class GlobalSignals
    {
        public static readonly Signal<string> TrainingModeChanged = new Signal<string>();
    }



}
