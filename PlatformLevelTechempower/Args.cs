using System;

namespace PlatformLevelTechempower
{
    public class Args
    {
        private Args()
        {

        }

        public bool Raw { get; set; }

        public int Port { get; set; } = 8081;

        public int ThreadCount { get; set; } = Environment.ProcessorCount;

        public static Args Parse(string[] args)
        {
            var namePrefix = "--";
            var result = new Args();

            for (int i = 0; i < args.Length; i++)
            {
                var name = args[i];
                if (string.Equals(namePrefix + nameof(Raw), name, StringComparison.OrdinalIgnoreCase))
                {
                    var value = args[i + 1];
                    if (bool.TryParse(value, out bool raw))
                    {
                        result.Raw = raw;
                    }
                    i++;
                    continue;
                }
                if (string.Equals(namePrefix + nameof(Port), name, StringComparison.OrdinalIgnoreCase))
                {
                    var value = args[i + 1];
                    if (int.TryParse(value, out int port))
                    {
                        result.Port = port;
                    }
                    i++;
                    continue;
                }
                if (string.Equals(namePrefix + nameof(ThreadCount), name, StringComparison.OrdinalIgnoreCase))
                {
                    var value = args[i + 1];
                    if (int.TryParse(value, out int threadCount))
                    {
                        result.ThreadCount = threadCount;
                    }
                    i++;
                    continue;
                }
            }

            return result;
        }
    }
}
