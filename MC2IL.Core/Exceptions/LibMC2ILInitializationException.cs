using System;

namespace MC2IL.Core.Exceptions;

public class LibMC2ILInitializationException(string message, Exception innerException)
    : Exception(message, innerException);
