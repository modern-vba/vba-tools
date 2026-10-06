using System;

if (args is not ["--trigger"])
{
    Console.Error.WriteLine("Controlled diagnostic fixture: pass --trigger to cause an intentional access violation.");
    return 2;
}

Console.Error.WriteLine("Starting controlled unhandled access violation.");
unsafe
{
    *(int*)1 = 42;
}

return 0;
