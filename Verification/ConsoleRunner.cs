using System;
using Drawmaha;
class ConsoleRunner
{
    static int Main()
    {
        try { Console.WriteLine(LogicChecks.Run()); return 0; }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
