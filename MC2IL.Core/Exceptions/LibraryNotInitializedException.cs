using System;

namespace MC2IL.Core.Exceptions;

public class LibraryNotInitializedException : Exception
{
    public override string Message => "This function requires LibMC2IL to be initialized - there is a function to do this in MC2ILApi.";
}
