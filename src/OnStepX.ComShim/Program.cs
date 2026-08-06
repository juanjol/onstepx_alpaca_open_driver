using System;

namespace OnStepX.ComShim
{
    /// <summary>
    /// Entry point of the COM local server.
    /// </summary>
    /// <remarks>
    /// The real plumbing, meaning the class factory, the registration and the four COM
    /// classes that act as an Alpaca client against localhost, is not written yet. This
    /// placeholder exists so the project builds and the solution stays complete.
    /// </remarks>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Console.WriteLine("OnStepX.ComShim: the COM local server is not implemented yet.");
            return 0;
        }
    }
}
