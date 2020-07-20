//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

#pragma once

[[cpp:dll-export:ICE_API]]
[[cpp:doxygen:include:Ice/Ice.h]]
[[cpp:header-ext:h]]

[[suppress-warning:reserved-identifier]]
[[js:module:ice]]

[[python:pkgdir:Ice]]

[[java:package:com.zeroc]]
[cs:namespace:ZeroC]
module Ice
{
    //
    // Note: The order of definitions here *must* match the order of
    // definitions for ::Slice::Operation::Mode in include/Slice/Parser.h!
    //

    /// Determines the retry behavior an invocation in case of a (potentially) recoverable error.
    enum OperationMode
    {
        /// Ordinary operations have <code>Normal</code> mode.  These operations
        /// modify object state; invoking such an operation twice in a row
        /// has different semantics than invoking it once. The Ice run time
        /// guarantees that it will not violate at-most-once semantics for
        /// <code>Normal</code> operations.
        Normal,

        /// Operations that use the Slice <code>nonmutating</code> keyword must not
        /// modify object state. For C++, nonmutating operations generate
        /// <code>const</code> member functions in the skeleton. In addition, the Ice
        /// run time will attempt to transparently recover from certain
        /// run-time errors by re-issuing a failed request and propagate
        /// the failure to the application only if the second attempt
        /// fails.
        ///
        /// <p class="Deprecated"><code>Nonmutating</code> is deprecated; Use the
        /// <code>idempotent</code> keyword instead. For C++, to retain the mapping
        /// of <code>nonmutating</code> operations to C++ <code>const</code>
        /// member functions, use the <code>\[cpp:const]</code> metadata
        /// directive.
        \Nonmutating,

        /// Operations that use the Slice <code>idempotent</code> keyword can modify
        /// object state, but invoking an operation twice in a row must
        /// result in the same object state as invoking it once.  For
        /// example, <code>x = 1</code> is an idempotent statement,
        /// whereas <code>x += 1</code> is not. For idempotent
        /// operations, the Ice run-time uses the same retry behavior
        /// as for nonmutating operations in case of a potentially
        /// recoverable error.
        \Idempotent
    }
}
