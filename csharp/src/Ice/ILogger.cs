//
// Copyright (c) ZeroC, Inc. All rights reserved.
//
//
namespace Ice
{
    public interface ILogger
    {
        /// <summary>Print a message, the message is printed literally, without any decorations such as executable
        /// name or time stamp.</summary>
        /// <param name="message">The message to log.</param>
        void Print(string message);

        /// <summary>Log a trace message.</summary>
        /// <param name="category">The trace category.</param>
        /// <param name="message">The trace message to log.</param>
        void Trace(string category, string message);

        /// <summary>Log a warning message.</summary>
        /// <param name="message">The warning message to log.</param>
        void Warning(string message);

        /// <summary>Log an error message.</summary>
        /// <param name="message">The error message to log.</param>
        void Error(string message);

        /// <summary>Returns this logger's prefix.</summary>
        /// <returns>The prefix.</returns>
        string GetPrefix();

        /// <summary>Returns a clone of the logger with a new prefix.</summary>
        /// <param name="prefix">The new prefix for the logger.</param>
        /// <returns>A logger instance.</returns>
        ILogger CloneWithPrefix(string prefix);
    }
}
