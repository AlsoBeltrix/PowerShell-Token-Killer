using System;
using System.Collections.Generic;

namespace Demo;

public class WidgetService
{
    public string Name { get; set; }

    public void Run()
    {
        Console.WriteLine("running");
    }
}

public interface IWidget
{
    void Run();
}
