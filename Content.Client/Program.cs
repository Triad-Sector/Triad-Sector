using System;
using Robust.Client;

namespace Content.Client
{
    internal static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // Use the public facade which internally invokes GameController with contentStart=true.
            // This avoids referencing the internal GameController type directly (fixes CS0122).
            ContentStart.Start(args);
        }
    }
}
