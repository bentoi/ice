# -*- coding: utf-8 -*-
#
# Copyright (c) ZeroC, Inc. All rights reserved.
#

# This test doesn't support running with IceSSL, the Router object in the client process uses
# the client certificate and fails with "unsupported certificate purpose"

if sys.version_info >= (3, 5):
    TestSuite(__name__)
