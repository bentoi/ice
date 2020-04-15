//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

using System;
using Test;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Ice.operations
{
    public class TwowaysAMI
    {
        private class CallbackBase
        {
            internal CallbackBase()
            {
                _called = false;
            }

            public virtual void check()
            {
                lock (this)
                {
                    while (!_called)
                    {
                        Monitor.Wait(this);
                    }
                    _called = false;
                }
            }

            public virtual void called()
            {
                lock (this)
                {
                    TestHelper.Assert(!_called);
                    _called = true;
                    Monitor.Pulse(this);
                }
            }

            public bool _called;
        }

        private class GenericCallback<T> : CallbackBase
        {
            public GenericCallback(T value)
            {
                this._value = value;
            }

            public void response(T value)
            {
                _value = value;
                _succeeded = true;
                called();
            }

            public void exception(Exception ex)
            {
                _succeeded = false;
                called();
            }

            public bool succeeded()
            {
                check();
                return _succeeded;
            }

            public T value()
            {
                return _value;
            }

            private T _value;
            private bool _succeeded = false;
        }

        private class Callback : CallbackBase
        {
            public Callback()
            {
            }

            public Callback(Communicator c) => _communicator = c;

            public Callback(int l) => _l = l;

            public Callback(Dictionary<string, string> d) => _d = d;

            public void opVoid() => called();

            public void opContext() => called();

            public void opByte(byte r, byte b)
            {
                TestHelper.Assert(b == 0xf0);
                TestHelper.Assert(r == 0xff);
                called();
            }

            public void opBool(bool r, bool b)
            {
                TestHelper.Assert(b);
                TestHelper.Assert(!r);
                called();
            }

            public void opShortIntLong(long r, short s, int i, long l)
            {
                TestHelper.Assert(s == 10);
                TestHelper.Assert(i == 11);
                TestHelper.Assert(l == 12);
                TestHelper.Assert(r == 12);
                called();
            }

            public void opFloatDouble(double r, float f, double d)
            {
                TestHelper.Assert(f == 3.14f);
                TestHelper.Assert(d == 1.1e10);
                TestHelper.Assert(r == 1.1e10);
                called();
            }

            public void opString(string r, string s)
            {
                TestHelper.Assert(s.Equals("world hello"));
                TestHelper.Assert(r.Equals("hello world"));
                called();
            }

            public void opMyEnum(Test.MyEnum r, Test.MyEnum e)
            {
                TestHelper.Assert(e == Test.MyEnum.enum2);
                TestHelper.Assert(r == Test.MyEnum.enum3);
                called();
            }

            public void opMyClass(Test.IMyClassPrx? r, Test.IMyClassPrx? c1, Test.IMyClassPrx? c2)
            {
                TestHelper.Assert(c1!.Identity.Equals(Identity.Parse("test")));
                TestHelper.Assert(c2!.Identity.Equals(Identity.Parse("noSuchIdentity")));
                TestHelper.Assert(r!.Identity.Equals(Identity.Parse("test")));

                //
                // We can't do the callbacks below in connection serialization mode.
                //
                if (_communicator!.GetPropertyAsInt("Ice.ThreadPool.Client.Serialize") == 0)
                {
                    r.opVoid();
                    c1.opVoid();
                    try
                    {
                        c2.opVoid();
                        TestHelper.Assert(false);
                    }
                    catch (ObjectNotExistException)
                    {
                    }
                }
                called();
            }

            public void opStruct(Test.Structure rso, Test.Structure so)
            {
                TestHelper.Assert(rso.p == null);
                TestHelper.Assert(rso.e == Test.MyEnum.enum2);
                TestHelper.Assert(rso.s.s.Equals("def"));
                TestHelper.Assert(so.e == Test.MyEnum.enum3);
                TestHelper.Assert(so.s.s.Equals("a new string"));

                //
                // We can't do the callbacks below in connection serialization mode.
                //
                if (_communicator!.GetPropertyAsInt("Ice.ThreadPool.Client.Serialize") == 0)
                {
                    so.p!.opVoid();
                }
                called();
            }

            public void opByteS(byte[] rso, byte[] bso)
            {
                TestHelper.Assert(bso.Length == 4);
                TestHelper.Assert(bso[0] == 0x22);
                TestHelper.Assert(bso[1] == 0x12);
                TestHelper.Assert(bso[2] == 0x11);
                TestHelper.Assert(bso[3] == 0x01);
                TestHelper.Assert(rso.Length == 8);
                TestHelper.Assert(rso[0] == 0x01);
                TestHelper.Assert(rso[1] == 0x11);
                TestHelper.Assert(rso[2] == 0x12);
                TestHelper.Assert(rso[3] == 0x22);
                TestHelper.Assert(rso[4] == 0xf1);
                TestHelper.Assert(rso[5] == 0xf2);
                TestHelper.Assert(rso[6] == 0xf3);
                TestHelper.Assert(rso[7] == 0xf4);
                called();
            }

            public void opBoolS(bool[] rso, bool[] bso)
            {
                TestHelper.Assert(bso.Length == 4);
                TestHelper.Assert(bso[0]);
                TestHelper.Assert(bso[1]);
                TestHelper.Assert(!bso[2]);
                TestHelper.Assert(!bso[3]);
                TestHelper.Assert(rso.Length == 3);
                TestHelper.Assert(!rso[0]);
                TestHelper.Assert(rso[1]);
                TestHelper.Assert(rso[2]);
                called();
            }

            public void opShortIntLongS(long[] rso, short[] sso, int[] iso, long[] lso)
            {
                TestHelper.Assert(sso.Length == 3);
                TestHelper.Assert(sso[0] == 1);
                TestHelper.Assert(sso[1] == 2);
                TestHelper.Assert(sso[2] == 3);
                TestHelper.Assert(iso.Length == 4);
                TestHelper.Assert(iso[0] == 8);
                TestHelper.Assert(iso[1] == 7);
                TestHelper.Assert(iso[2] == 6);
                TestHelper.Assert(iso[3] == 5);
                TestHelper.Assert(lso.Length == 6);
                TestHelper.Assert(lso[0] == 10);
                TestHelper.Assert(lso[1] == 30);
                TestHelper.Assert(lso[2] == 20);
                TestHelper.Assert(lso[3] == 10);
                TestHelper.Assert(lso[4] == 30);
                TestHelper.Assert(lso[5] == 20);
                TestHelper.Assert(rso.Length == 3);
                TestHelper.Assert(rso[0] == 10);
                TestHelper.Assert(rso[1] == 30);
                TestHelper.Assert(rso[2] == 20);
                called();
            }

            public void opFloatDoubleS(double[] rso, float[] fso, double[] dso)
            {
                TestHelper.Assert(fso.Length == 2);
                TestHelper.Assert(fso[0] == 3.14f);
                TestHelper.Assert(fso[1] == 1.11f);
                TestHelper.Assert(dso.Length == 3);
                TestHelper.Assert(dso[0] == 1.3e10);
                TestHelper.Assert(dso[1] == 1.2e10);
                TestHelper.Assert(dso[2] == 1.1e10);
                TestHelper.Assert(rso.Length == 5);
                TestHelper.Assert(rso[0] == 1.1e10);
                TestHelper.Assert(rso[1] == 1.2e10);
                TestHelper.Assert(rso[2] == 1.3e10);
                TestHelper.Assert((float)rso[3] == 3.14f);
                TestHelper.Assert((float)rso[4] == 1.11f);
                called();
            }

            public void opStringS(string[] rso, string[] sso)
            {
                TestHelper.Assert(sso.Length == 4);
                TestHelper.Assert(sso[0].Equals("abc"));
                TestHelper.Assert(sso[1].Equals("de"));
                TestHelper.Assert(sso[2].Equals("fghi"));
                TestHelper.Assert(sso[3].Equals("xyz"));
                TestHelper.Assert(rso.Length == 3);
                TestHelper.Assert(rso[0].Equals("fghi"));
                TestHelper.Assert(rso[1].Equals("de"));
                TestHelper.Assert(rso[2].Equals("abc"));
                called();
            }

            public void opByteSS(byte[][] rso, byte[][] bso)
            {
                TestHelper.Assert(bso.Length == 2);
                TestHelper.Assert(bso[0].Length == 1);
                TestHelper.Assert(bso[0][0] == 0xff);
                TestHelper.Assert(bso[1].Length == 3);
                TestHelper.Assert(bso[1][0] == 0x01);
                TestHelper.Assert(bso[1][1] == 0x11);
                TestHelper.Assert(bso[1][2] == 0x12);
                TestHelper.Assert(rso.Length == 4);
                TestHelper.Assert(rso[0].Length == 3);
                TestHelper.Assert(rso[0][0] == 0x01);
                TestHelper.Assert(rso[0][1] == 0x11);
                TestHelper.Assert(rso[0][2] == 0x12);
                TestHelper.Assert(rso[1].Length == 1);
                TestHelper.Assert(rso[1][0] == 0xff);
                TestHelper.Assert(rso[2].Length == 1);
                TestHelper.Assert(rso[2][0] == 0x0e);
                TestHelper.Assert(rso[3].Length == 2);
                TestHelper.Assert(rso[3][0] == 0xf2);
                TestHelper.Assert(rso[3][1] == 0xf1);
                called();
            }

            public void opBoolSS(bool[][] rso, bool[][] bso)
            {
                TestHelper.Assert(bso.Length == 4);
                TestHelper.Assert(bso[0].Length == 1);
                TestHelper.Assert(bso[0][0]);
                TestHelper.Assert(bso[1].Length == 1);
                TestHelper.Assert(!bso[1][0]);
                TestHelper.Assert(bso[2].Length == 2);
                TestHelper.Assert(bso[2][0]);
                TestHelper.Assert(bso[2][1]);
                TestHelper.Assert(bso[3].Length == 3);
                TestHelper.Assert(!bso[3][0]);
                TestHelper.Assert(!bso[3][1]);
                TestHelper.Assert(bso[3][2]);
                TestHelper.Assert(rso.Length == 3);
                TestHelper.Assert(rso[0].Length == 2);
                TestHelper.Assert(rso[0][0]);
                TestHelper.Assert(rso[0][1]);
                TestHelper.Assert(rso[1].Length == 1);
                TestHelper.Assert(!rso[1][0]);
                TestHelper.Assert(rso[2].Length == 1);
                TestHelper.Assert(rso[2][0]);
                called();
            }

            public void opShortIntLongSS(long[][] rso, short[][] sso, int[][] iso, long[][] lso)
            {
                TestHelper.Assert(rso.Length == 1);
                TestHelper.Assert(rso[0].Length == 2);
                TestHelper.Assert(rso[0][0] == 496);
                TestHelper.Assert(rso[0][1] == 1729);
                TestHelper.Assert(sso.Length == 3);
                TestHelper.Assert(sso[0].Length == 3);
                TestHelper.Assert(sso[0][0] == 1);
                TestHelper.Assert(sso[0][1] == 2);
                TestHelper.Assert(sso[0][2] == 5);
                TestHelper.Assert(sso[1].Length == 1);
                TestHelper.Assert(sso[1][0] == 13);
                TestHelper.Assert(sso[2].Length == 0);
                TestHelper.Assert(iso.Length == 2);
                TestHelper.Assert(iso[0].Length == 1);
                TestHelper.Assert(iso[0][0] == 42);
                TestHelper.Assert(iso[1].Length == 2);
                TestHelper.Assert(iso[1][0] == 24);
                TestHelper.Assert(iso[1][1] == 98);
                TestHelper.Assert(lso.Length == 2);
                TestHelper.Assert(lso[0].Length == 2);
                TestHelper.Assert(lso[0][0] == 496);
                TestHelper.Assert(lso[0][1] == 1729);
                TestHelper.Assert(lso[1].Length == 2);
                TestHelper.Assert(lso[1][0] == 496);
                TestHelper.Assert(lso[1][1] == 1729);
                called();
            }

            public void opFloatDoubleSS(double[][] rso, float[][] fso, double[][] dso)
            {
                TestHelper.Assert(fso.Length == 3);
                TestHelper.Assert(fso[0].Length == 1);
                TestHelper.Assert(fso[0][0] == 3.14f);
                TestHelper.Assert(fso[1].Length == 1);
                TestHelper.Assert(fso[1][0] == 1.11f);
                TestHelper.Assert(fso[2].Length == 0);
                TestHelper.Assert(dso.Length == 1);
                TestHelper.Assert(dso[0].Length == 3);
                TestHelper.Assert(dso[0][0] == 1.1e10);
                TestHelper.Assert(dso[0][1] == 1.2e10);
                TestHelper.Assert(dso[0][2] == 1.3e10);
                TestHelper.Assert(rso.Length == 2);
                TestHelper.Assert(rso[0].Length == 3);
                TestHelper.Assert(rso[0][0] == 1.1e10);
                TestHelper.Assert(rso[0][1] == 1.2e10);
                TestHelper.Assert(rso[0][2] == 1.3e10);
                TestHelper.Assert(rso[1].Length == 3);
                TestHelper.Assert(rso[1][0] == 1.1e10);
                TestHelper.Assert(rso[1][1] == 1.2e10);
                TestHelper.Assert(rso[1][2] == 1.3e10);
                called();
            }

            public void opStringSS(string[][] rso, string[][] sso)
            {
                TestHelper.Assert(sso.Length == 5);
                TestHelper.Assert(sso[0].Length == 1);
                TestHelper.Assert(sso[0][0].Equals("abc"));
                TestHelper.Assert(sso[1].Length == 2);
                TestHelper.Assert(sso[1][0].Equals("de"));
                TestHelper.Assert(sso[1][1].Equals("fghi"));
                TestHelper.Assert(sso[2].Length == 0);
                TestHelper.Assert(sso[3].Length == 0);
                TestHelper.Assert(sso[4].Length == 1);
                TestHelper.Assert(sso[4][0].Equals("xyz"));
                TestHelper.Assert(rso.Length == 3);
                TestHelper.Assert(rso[0].Length == 1);
                TestHelper.Assert(rso[0][0].Equals("xyz"));
                TestHelper.Assert(rso[1].Length == 0);
                TestHelper.Assert(rso[2].Length == 0);
                called();
            }

            public void opStringSSS(string[][][] rsso, string[][][] ssso)
            {
                TestHelper.Assert(ssso.Length == 5);
                TestHelper.Assert(ssso[0].Length == 2);
                TestHelper.Assert(ssso[0][0].Length == 2);
                TestHelper.Assert(ssso[0][1].Length == 1);
                TestHelper.Assert(ssso[1].Length == 1);
                TestHelper.Assert(ssso[1][0].Length == 1);
                TestHelper.Assert(ssso[2].Length == 2);
                TestHelper.Assert(ssso[2][0].Length == 2);
                TestHelper.Assert(ssso[2][1].Length == 1);
                TestHelper.Assert(ssso[3].Length == 1);
                TestHelper.Assert(ssso[3][0].Length == 1);
                TestHelper.Assert(ssso[4].Length == 0);
                TestHelper.Assert(ssso[0][0][0].Equals("abc"));
                TestHelper.Assert(ssso[0][0][1].Equals("de"));
                TestHelper.Assert(ssso[0][1][0].Equals("xyz"));
                TestHelper.Assert(ssso[1][0][0].Equals("hello"));
                TestHelper.Assert(ssso[2][0][0].Equals(""));
                TestHelper.Assert(ssso[2][0][1].Equals(""));
                TestHelper.Assert(ssso[2][1][0].Equals("abcd"));
                TestHelper.Assert(ssso[3][0][0].Equals(""));

                TestHelper.Assert(rsso.Length == 3);
                TestHelper.Assert(rsso[0].Length == 0);
                TestHelper.Assert(rsso[1].Length == 1);
                TestHelper.Assert(rsso[1][0].Length == 1);
                TestHelper.Assert(rsso[2].Length == 2);
                TestHelper.Assert(rsso[2][0].Length == 2);
                TestHelper.Assert(rsso[2][1].Length == 1);
                TestHelper.Assert(rsso[1][0][0].Equals(""));
                TestHelper.Assert(rsso[2][0][0].Equals(""));
                TestHelper.Assert(rsso[2][0][1].Equals(""));
                TestHelper.Assert(rsso[2][1][0].Equals("abcd"));
                called();
            }

            public void opByteBoolD(Dictionary<byte, bool> ro, Dictionary<byte, bool> _do)
            {
                Dictionary<byte, bool> di1 = new Dictionary<byte, bool>();
                di1[10] = true;
                di1[100] = false;
                TestHelper.Assert(Collections.Equals(_do, di1));
                TestHelper.Assert(ro.Count == 4);
                TestHelper.Assert(ro[10] == true);
                TestHelper.Assert(ro[11] == false);
                TestHelper.Assert(ro[100] == false);
                TestHelper.Assert(ro[101] == true);
                called();
            }

            public void opShortIntD(Dictionary<short, int> ro, Dictionary<short, int> _do)
            {
                Dictionary<short, int> di1 = new Dictionary<short, int>();
                di1[110] = -1;
                di1[1100] = 123123;
                TestHelper.Assert(Collections.Equals(_do, di1));
                TestHelper.Assert(ro.Count == 4);
                TestHelper.Assert(ro[110] == -1);
                TestHelper.Assert(ro[111] == -100);
                TestHelper.Assert(ro[1100] == 123123);
                TestHelper.Assert(ro[1101] == 0);
                called();
            }

            public void opLongFloatD(Dictionary<long, float> ro, Dictionary<long, float> _do)
            {
                Dictionary<long, float> di1 = new Dictionary<long, float>();
                di1[999999110L] = -1.1f;
                di1[999999111L] = 123123.2f;
                TestHelper.Assert(Collections.Equals(_do, di1));
                TestHelper.Assert(ro.Count == 4);
                TestHelper.Assert(ro[999999110L] == -1.1f);
                TestHelper.Assert(ro[999999120L] == -100.4f);
                TestHelper.Assert(ro[999999111L] == 123123.2f);
                TestHelper.Assert(ro[999999130L] == 0.5f);
                called();
            }

            public void opStringStringD(Dictionary<string, string> ro, Dictionary<string, string> _do)
            {
                Dictionary<string, string> di1 = new Dictionary<string, string>();
                di1["foo"] = "abc -1.1";
                di1["bar"] = "abc 123123.2";
                TestHelper.Assert(Collections.Equals(_do, di1));
                TestHelper.Assert(ro.Count == 4);
                TestHelper.Assert(ro["foo"].Equals("abc -1.1"));
                TestHelper.Assert(ro["FOO"].Equals("abc -100.4"));
                TestHelper.Assert(ro["bar"].Equals("abc 123123.2"));
                TestHelper.Assert(ro["BAR"].Equals("abc 0.5"));
                called();
            }

            public void opStringMyEnumD(Dictionary<string, Test.MyEnum> ro, Dictionary<string, Test.MyEnum> _do)
            {
                var di1 = new Dictionary<string, Test.MyEnum>();
                di1["abc"] = Test.MyEnum.enum1;
                di1[""] = Test.MyEnum.enum2;
                TestHelper.Assert(Collections.Equals(_do, di1));
                TestHelper.Assert(ro.Count == 4);
                TestHelper.Assert(ro["abc"] == Test.MyEnum.enum1);
                TestHelper.Assert(ro["qwerty"] == Test.MyEnum.enum3);
                TestHelper.Assert(ro[""] == Test.MyEnum.enum2);
                TestHelper.Assert(ro["Hello!!"] == Test.MyEnum.enum2);
                called();
            }

            public void opMyEnumStringD(Dictionary<Test.MyEnum, string> ro, Dictionary<Test.MyEnum, string> _do)
            {
                var di1 = new Dictionary<Test.MyEnum, string>();
                di1[Test.MyEnum.enum1] = "abc";
                TestHelper.Assert(Collections.Equals(_do, di1));
                TestHelper.Assert(ro.Count == 3);
                TestHelper.Assert(ro[Test.MyEnum.enum1].Equals("abc"));
                TestHelper.Assert(ro[Test.MyEnum.enum2].Equals("Hello!!"));
                TestHelper.Assert(ro[Test.MyEnum.enum3].Equals("qwerty"));
                called();
            }

            public void opMyStructMyEnumD(Dictionary<Test.MyStruct, Test.MyEnum> ro,
                                            Dictionary<Test.MyStruct, Test.MyEnum> _do)
            {
                var s11 = new Test.MyStruct(1, 1);
                var s12 = new Test.MyStruct(1, 2);
                var di1 = new Dictionary<Test.MyStruct, Test.MyEnum>();
                di1[s11] = Test.MyEnum.enum1;
                di1[s12] = Test.MyEnum.enum2;
                TestHelper.Assert(Collections.Equals(_do, di1));
                var s22 = new Test.MyStruct(2, 2);
                var s23 = new Test.MyStruct(2, 3);
                TestHelper.Assert(ro.Count == 4);
                TestHelper.Assert(ro[s11] == Test.MyEnum.enum1);
                TestHelper.Assert(ro[s12] == Test.MyEnum.enum2);
                TestHelper.Assert(ro[s22] == Test.MyEnum.enum3);
                TestHelper.Assert(ro[s23] == Test.MyEnum.enum2);
                called();
            }

            public void opByteBoolDS(Dictionary<byte, bool>[] ro,
                                        Dictionary<byte, bool>[] _do)
            {
                TestHelper.Assert(ro.Length == 2);
                TestHelper.Assert(ro[0].Count == 3);
                TestHelper.Assert(ro[0][10]);
                TestHelper.Assert(!ro[0][11]);
                TestHelper.Assert(ro[0][101]);
                TestHelper.Assert(ro[1].Count == 2);
                TestHelper.Assert(ro[1][10]);
                TestHelper.Assert(!ro[1][100]);

                TestHelper.Assert(_do.Length == 3);
                TestHelper.Assert(_do[0].Count == 2);
                TestHelper.Assert(!_do[0][100]);
                TestHelper.Assert(!_do[0][101]);
                TestHelper.Assert(_do[1].Count == 2);
                TestHelper.Assert(_do[1][10]);
                TestHelper.Assert(!_do[1][100]);
                TestHelper.Assert(_do[2].Count == 3);
                TestHelper.Assert(_do[2][10]);
                TestHelper.Assert(!_do[2][11]);
                TestHelper.Assert(_do[2][101]);
                called();
            }

            public void opShortIntDS(Dictionary<short, int>[] ro,
                                        Dictionary<short, int>[] _do)
            {
                TestHelper.Assert(ro.Length == 2);
                TestHelper.Assert(ro[0].Count == 3);
                TestHelper.Assert(ro[0][110] == -1);
                TestHelper.Assert(ro[0][111] == -100);
                TestHelper.Assert(ro[0][1101] == 0);
                TestHelper.Assert(ro[1].Count == 2);
                TestHelper.Assert(ro[1][110] == -1);
                TestHelper.Assert(ro[1][1100] == 123123);

                TestHelper.Assert(_do.Length == 3);
                TestHelper.Assert(_do[0].Count == 1);
                TestHelper.Assert(_do[0][100] == -1001);
                TestHelper.Assert(_do[1].Count == 2);
                TestHelper.Assert(_do[1][110] == -1);
                TestHelper.Assert(_do[1][1100] == 123123);
                TestHelper.Assert(_do[2].Count == 3);
                TestHelper.Assert(_do[2][110] == -1);
                TestHelper.Assert(_do[2][111] == -100);
                TestHelper.Assert(_do[2][1101] == 0);
                called();
            }

            public void opLongFloatDS(Dictionary<long, float>[] ro,
                                        Dictionary<long, float>[] _do)
            {
                TestHelper.Assert(ro.Length == 2);
                TestHelper.Assert(ro[0].Count == 3);
                TestHelper.Assert(ro[0][999999110L] == -1.1f);
                TestHelper.Assert(ro[0][999999120L] == -100.4f);
                TestHelper.Assert(ro[0][999999130L] == 0.5f);
                TestHelper.Assert(ro[1].Count == 2);
                TestHelper.Assert(ro[1][999999110L] == -1.1f);
                TestHelper.Assert(ro[1][999999111L] == 123123.2f);

                TestHelper.Assert(_do.Length == 3);
                TestHelper.Assert(_do[0].Count == 1);
                TestHelper.Assert(_do[0][999999140L] == 3.14f);
                TestHelper.Assert(_do[1].Count == 2);
                TestHelper.Assert(_do[1][999999110L] == -1.1f);
                TestHelper.Assert(_do[1][999999111L] == 123123.2f);
                TestHelper.Assert(_do[2].Count == 3);
                TestHelper.Assert(_do[2][999999110L] == -1.1f);
                TestHelper.Assert(_do[2][999999120L] == -100.4f);
                TestHelper.Assert(_do[2][999999130L] == 0.5f);
                called();
            }

            public void opStringStringDS(Dictionary<string, string>[] ro,
                                            Dictionary<string, string>[] _do)
            {
                TestHelper.Assert(ro.Length == 2);
                TestHelper.Assert(ro[0].Count == 3);
                TestHelper.Assert(ro[0]["foo"].Equals("abc -1.1"));
                TestHelper.Assert(ro[0]["FOO"].Equals("abc -100.4"));
                TestHelper.Assert(ro[0]["BAR"].Equals("abc 0.5"));
                TestHelper.Assert(ro[1].Count == 2);
                TestHelper.Assert(ro[1]["foo"] == "abc -1.1");
                TestHelper.Assert(ro[1]["bar"] == "abc 123123.2");

                TestHelper.Assert(_do.Length == 3);
                TestHelper.Assert(_do[0].Count == 1);
                TestHelper.Assert(_do[0]["f00"].Equals("ABC -3.14"));
                TestHelper.Assert(_do[1].Count == 2);
                TestHelper.Assert(_do[1]["foo"].Equals("abc -1.1"));
                TestHelper.Assert(_do[1]["bar"].Equals("abc 123123.2"));
                TestHelper.Assert(_do[2].Count == 3);
                TestHelper.Assert(_do[2]["foo"].Equals("abc -1.1"));
                TestHelper.Assert(_do[2]["FOO"].Equals("abc -100.4"));
                TestHelper.Assert(_do[2]["BAR"].Equals("abc 0.5"));
                called();
            }

            public void opStringMyEnumDS(Dictionary<string, Test.MyEnum>[] ro,
                                            Dictionary<string, Test.MyEnum>[] _do)
            {
                TestHelper.Assert(ro.Length == 2);
                TestHelper.Assert(ro[0].Count == 3);
                TestHelper.Assert(ro[0]["abc"] == Test.MyEnum.enum1);
                TestHelper.Assert(ro[0]["qwerty"] == Test.MyEnum.enum3);
                TestHelper.Assert(ro[0]["Hello!!"] == Test.MyEnum.enum2);
                TestHelper.Assert(ro[1].Count == 2);
                TestHelper.Assert(ro[1]["abc"] == Test.MyEnum.enum1);
                TestHelper.Assert(ro[1][""] == Test.MyEnum.enum2);

                TestHelper.Assert(_do.Length == 3);
                TestHelper.Assert(_do[0].Count == 1);
                TestHelper.Assert(_do[0]["Goodbye"] == Test.MyEnum.enum1);
                TestHelper.Assert(_do[1].Count == 2);
                TestHelper.Assert(_do[1]["abc"] == Test.MyEnum.enum1);
                TestHelper.Assert(_do[1][""] == Test.MyEnum.enum2);
                TestHelper.Assert(_do[2].Count == 3);
                TestHelper.Assert(_do[2]["abc"] == Test.MyEnum.enum1);
                TestHelper.Assert(_do[2]["qwerty"] == Test.MyEnum.enum3);
                TestHelper.Assert(_do[2]["Hello!!"] == Test.MyEnum.enum2);
                called();
            }

            public void opMyEnumStringDS(Dictionary<Test.MyEnum, string>[] ro,
                                            Dictionary<Test.MyEnum, string>[] _do)
            {
                TestHelper.Assert(ro.Length == 2);
                TestHelper.Assert(ro[0].Count == 2);
                TestHelper.Assert(ro[0][Test.MyEnum.enum2].Equals("Hello!!"));
                TestHelper.Assert(ro[0][Test.MyEnum.enum3].Equals("qwerty"));
                TestHelper.Assert(ro[1].Count == 1);
                TestHelper.Assert(ro[1][Test.MyEnum.enum1].Equals("abc"));

                TestHelper.Assert(_do.Length == 3);
                TestHelper.Assert(_do[0].Count == 1);
                TestHelper.Assert(_do[0][Test.MyEnum.enum1].Equals("Goodbye"));
                TestHelper.Assert(_do[1].Count == 1);
                TestHelper.Assert(_do[1][Test.MyEnum.enum1].Equals("abc"));
                TestHelper.Assert(_do[2].Count == 2);
                TestHelper.Assert(_do[2][Test.MyEnum.enum2].Equals("Hello!!"));
                TestHelper.Assert(_do[2][Test.MyEnum.enum3].Equals("qwerty"));
                called();
            }

            public void opMyStructMyEnumDS(Dictionary<Test.MyStruct, Test.MyEnum>[] ro,
                                            Dictionary<Test.MyStruct, Test.MyEnum>[] _do)
            {
                var s11 = new Test.MyStruct(1, 1);
                var s12 = new Test.MyStruct(1, 2);
                var s22 = new Test.MyStruct(2, 2);
                var s23 = new Test.MyStruct(2, 3);

                TestHelper.Assert(ro.Length == 2);
                TestHelper.Assert(ro[0].Count == 3);
                TestHelper.Assert(ro[0][s11] == Test.MyEnum.enum1);
                TestHelper.Assert(ro[0][s22] == Test.MyEnum.enum3);
                TestHelper.Assert(ro[0][s23] == Test.MyEnum.enum2);
                TestHelper.Assert(ro[1].Count == 2);
                TestHelper.Assert(ro[1][s11] == Test.MyEnum.enum1);
                TestHelper.Assert(ro[1][s12] == Test.MyEnum.enum2);

                TestHelper.Assert(_do.Length == 3);
                TestHelper.Assert(_do[0].Count == 1);
                TestHelper.Assert(_do[0][s23] == Test.MyEnum.enum3);
                TestHelper.Assert(_do[1].Count == 2);
                TestHelper.Assert(_do[1][s11] == Test.MyEnum.enum1);
                TestHelper.Assert(_do[1][s12] == Test.MyEnum.enum2);
                TestHelper.Assert(_do[2].Count == 3);
                TestHelper.Assert(_do[2][s11] == Test.MyEnum.enum1);
                TestHelper.Assert(_do[2][s22] == Test.MyEnum.enum3);
                TestHelper.Assert(_do[2][s23] == Test.MyEnum.enum2);
                called();
            }

            public void opByteByteSD(Dictionary<byte, byte[]> ro,
                                        Dictionary<byte, byte[]> _do)
            {
                TestHelper.Assert(_do.Count == 1);
                TestHelper.Assert(_do[0xf1].Length == 2);
                TestHelper.Assert(_do[0xf1][0] == 0xf2);
                TestHelper.Assert(_do[0xf1][1] == 0xf3);

                TestHelper.Assert(ro.Count == 3);
                TestHelper.Assert(ro[0x01].Length == 2);
                TestHelper.Assert(ro[0x01][0] == 0x01);
                TestHelper.Assert(ro[0x01][1] == 0x11);
                TestHelper.Assert(ro[0x22].Length == 1);
                TestHelper.Assert(ro[0x22][0] == 0x12);
                TestHelper.Assert(ro[0xf1].Length == 2);
                TestHelper.Assert(ro[0xf1][0] == 0xf2);
                TestHelper.Assert(ro[0xf1][1] == 0xf3);
                called();
            }

            public void opBoolBoolSD(Dictionary<bool, bool[]> ro,
                                        Dictionary<bool, bool[]> _do)
            {
                TestHelper.Assert(_do.Count == 1);
                TestHelper.Assert(_do[false].Length == 2);
                TestHelper.Assert(_do[false][0]);
                TestHelper.Assert(!_do[false][1]);
                TestHelper.Assert(ro.Count == 2);
                TestHelper.Assert(ro[false].Length == 2);
                TestHelper.Assert(ro[false][0]);
                TestHelper.Assert(!ro[false][1]);
                TestHelper.Assert(ro[true].Length == 3);
                TestHelper.Assert(!ro[true][0]);
                TestHelper.Assert(ro[true][1]);
                TestHelper.Assert(ro[true][2]);
                called();
            }

            public void opShortShortSD(Dictionary<short, short[]> ro,
                                        Dictionary<short, short[]> _do)
            {
                TestHelper.Assert(_do.Count == 1);
                TestHelper.Assert(_do[4].Length == 2);
                TestHelper.Assert(_do[4][0] == 6);
                TestHelper.Assert(_do[4][1] == 7);

                TestHelper.Assert(ro.Count == 3);
                TestHelper.Assert(ro[1].Length == 3);
                TestHelper.Assert(ro[1][0] == 1);
                TestHelper.Assert(ro[1][1] == 2);
                TestHelper.Assert(ro[1][2] == 3);
                TestHelper.Assert(ro[2].Length == 2);
                TestHelper.Assert(ro[2][0] == 4);
                TestHelper.Assert(ro[2][1] == 5);
                TestHelper.Assert(ro[4].Length == 2);
                TestHelper.Assert(ro[4][0] == 6);
                TestHelper.Assert(ro[4][1] == 7);
                called();
            }

            public void opIntIntSD(Dictionary<int, int[]> ro,
                                    Dictionary<int, int[]> _do)
            {
                TestHelper.Assert(_do.Count == 1);
                TestHelper.Assert(_do[400].Length == 2);
                TestHelper.Assert(_do[400][0] == 600);
                TestHelper.Assert(_do[400][1] == 700);

                TestHelper.Assert(ro.Count == 3);
                TestHelper.Assert(ro[100].Length == 3);
                TestHelper.Assert(ro[100][0] == 100);
                TestHelper.Assert(ro[100][1] == 200);
                TestHelper.Assert(ro[100][2] == 300);
                TestHelper.Assert(ro[200].Length == 2);
                TestHelper.Assert(ro[200][0] == 400);
                TestHelper.Assert(ro[200][1] == 500);
                TestHelper.Assert(ro[400].Length == 2);
                TestHelper.Assert(ro[400][0] == 600);
                TestHelper.Assert(ro[400][1] == 700);
                called();
            }

            public void opLongLongSD(Dictionary<long, long[]> ro,
                                        Dictionary<long, long[]> _do)
            {
                TestHelper.Assert(_do.Count == 1);
                TestHelper.Assert(_do[999999992L].Length == 2);
                TestHelper.Assert(_do[999999992L][0] == 999999110L);
                TestHelper.Assert(_do[999999992L][1] == 999999120L);
                TestHelper.Assert(ro.Count == 3);
                TestHelper.Assert(ro[999999990L].Length == 3);
                TestHelper.Assert(ro[999999990L][0] == 999999110L);
                TestHelper.Assert(ro[999999990L][1] == 999999111L);
                TestHelper.Assert(ro[999999990L][2] == 999999110L);
                TestHelper.Assert(ro[999999991L].Length == 2);
                TestHelper.Assert(ro[999999991L][0] == 999999120L);
                TestHelper.Assert(ro[999999991L][1] == 999999130L);
                TestHelper.Assert(ro[999999992L].Length == 2);
                TestHelper.Assert(ro[999999992L][0] == 999999110L);
                TestHelper.Assert(ro[999999992L][1] == 999999120L);
                called();
            }

            public void opStringFloatSD(Dictionary<string, float[]> ro,
                                        Dictionary<string, float[]> _do)
            {
                TestHelper.Assert(_do.Count == 1);
                TestHelper.Assert(_do["aBc"].Length == 2);
                TestHelper.Assert(_do["aBc"][0] == -3.14f);
                TestHelper.Assert(_do["aBc"][1] == 3.14f);

                TestHelper.Assert(ro.Count == 3);
                TestHelper.Assert(ro["abc"].Length == 3);
                TestHelper.Assert(ro["abc"][0] == -1.1f);
                TestHelper.Assert(ro["abc"][1] == 123123.2f);
                TestHelper.Assert(ro["abc"][2] == 100.0f);
                TestHelper.Assert(ro["ABC"].Length == 2);
                TestHelper.Assert(ro["ABC"][0] == 42.24f);
                TestHelper.Assert(ro["ABC"][1] == -1.61f);
                TestHelper.Assert(ro["aBc"].Length == 2);
                TestHelper.Assert(ro["aBc"][0] == -3.14f);
                TestHelper.Assert(ro["aBc"][1] == 3.14f);
                called();
            }

            public void opStringDoubleSD(Dictionary<string, double[]> ro,
                                            Dictionary<string, double[]> _do)
            {
                TestHelper.Assert(_do.Count == 1);
                TestHelper.Assert(_do[""].Length == 2);
                TestHelper.Assert(_do[""][0] == 1.6E10);
                TestHelper.Assert(_do[""][1] == 1.7E10);
                TestHelper.Assert(ro.Count == 3);
                TestHelper.Assert(ro["Hello!!"].Length == 3);
                TestHelper.Assert(ro["Hello!!"][0] == 1.1E10);
                TestHelper.Assert(ro["Hello!!"][1] == 1.2E10);
                TestHelper.Assert(ro["Hello!!"][2] == 1.3E10);
                TestHelper.Assert(ro["Goodbye"].Length == 2);
                TestHelper.Assert(ro["Goodbye"][0] == 1.4E10);
                TestHelper.Assert(ro["Goodbye"][1] == 1.5E10);
                TestHelper.Assert(ro[""].Length == 2);
                TestHelper.Assert(ro[""][0] == 1.6E10);
                TestHelper.Assert(ro[""][1] == 1.7E10);
                called();
            }

            public void opStringStringSD(Dictionary<string, string[]> ro,
                                            Dictionary<string, string[]> _do)
            {
                TestHelper.Assert(_do.Count == 1);
                TestHelper.Assert(_do["ghi"].Length == 2);
                TestHelper.Assert(_do["ghi"][0].Equals("and"));
                TestHelper.Assert(_do["ghi"][1].Equals("xor"));

                TestHelper.Assert(ro.Count == 3);
                TestHelper.Assert(ro["abc"].Length == 3);
                TestHelper.Assert(ro["abc"][0].Equals("abc"));
                TestHelper.Assert(ro["abc"][1].Equals("de"));
                TestHelper.Assert(ro["abc"][2].Equals("fghi"));
                TestHelper.Assert(ro["def"].Length == 2);
                TestHelper.Assert(ro["def"][0].Equals("xyz"));
                TestHelper.Assert(ro["def"][1].Equals("or"));
                TestHelper.Assert(ro["ghi"].Length == 2);
                TestHelper.Assert(ro["ghi"][0].Equals("and"));
                TestHelper.Assert(ro["ghi"][1].Equals("xor"));
                called();
            }

            public void opMyEnumMyEnumSD(Dictionary<Test.MyEnum, Test.MyEnum[]> ro,
                                            Dictionary<Test.MyEnum, Test.MyEnum[]> _do)
            {
                TestHelper.Assert(_do.Count == 1);
                TestHelper.Assert(_do[Test.MyEnum.enum1].Length == 2);
                TestHelper.Assert(_do[Test.MyEnum.enum1][0] == Test.MyEnum.enum3);
                TestHelper.Assert(_do[Test.MyEnum.enum1][1] == Test.MyEnum.enum3);
                TestHelper.Assert(ro.Count == 3);
                TestHelper.Assert(ro[Test.MyEnum.enum3].Length == 3);
                TestHelper.Assert(ro[Test.MyEnum.enum3][0] == Test.MyEnum.enum1);
                TestHelper.Assert(ro[Test.MyEnum.enum3][1] == Test.MyEnum.enum1);
                TestHelper.Assert(ro[Test.MyEnum.enum3][2] == Test.MyEnum.enum2);
                TestHelper.Assert(ro[Test.MyEnum.enum2].Length == 2);
                TestHelper.Assert(ro[Test.MyEnum.enum2][0] == Test.MyEnum.enum1);
                TestHelper.Assert(ro[Test.MyEnum.enum2][1] == Test.MyEnum.enum2);
                TestHelper.Assert(ro[Test.MyEnum.enum1].Length == 2);
                TestHelper.Assert(ro[Test.MyEnum.enum1][0] == Test.MyEnum.enum3);
                TestHelper.Assert(ro[Test.MyEnum.enum1][1] == Test.MyEnum.enum3);
                called();
            }

            public void opIntS(int[] r)
            {
                TestHelper.Assert(r.Length == _l);
                for (int j = 0; j < r.Length; ++j)
                {
                    TestHelper.Assert(r[j] == -j);
                }
                called();
            }

            public void opContextNotEqual(Dictionary<string, string> r)
            {
                TestHelper.Assert(!Collections.Equals(r, _d));
                called();
            }

            public void opContextEqual(Dictionary<string, string> r)
            {
                TestHelper.Assert(Collections.Equals(r, _d));
                called();
            }

            public void opIdempotent() => called();

            public void OpNonmutating() => called();

            public void opDerived() => called();

            public void exCB(Exception ex) => TestHelper.Assert(false);

            private Communicator? _communicator;
            private readonly int _l;
            private readonly Dictionary<string, string>? _d;
        }

        internal static void twowaysAMI(TestHelper helper, Test.IMyClassPrx p)
        {
            Communicator? communicator = helper.Communicator();
            TestHelper.Assert(communicator != null);

            {
                p.IcePingAsync().Wait();
            }

            {
                TestHelper.Assert(p.IceIsAAsync("::Test::MyClass").Result);
            }

            {
                TestHelper.Assert(p.IceIdsAsync().Result.Length == 3);
            }

            {
                TestHelper.Assert(p.IceIdAsync().Result.Equals("::Test::MyDerivedClass"));
            }

            {
                p.opVoidAsync().Wait();
            }

            {
                var ret = p.opByteAsync(0xff, 0x0f).Result;
                TestHelper.Assert(ret.p3 == 0xf0);
                TestHelper.Assert(ret.ReturnValue == 0xff);
            }

            {
                var cb = new Callback();
                var ret = p.opBoolAsync(true, false).Result;
                cb.opBool(ret.ReturnValue, ret.p3);
            }

            {
                var cb = new Callback();
                var ret = p.opShortIntLongAsync(10, 11, 12).Result;
                cb.opShortIntLong(ret.ReturnValue, ret.p4, ret.p5, ret.p6);
            }

            {
                var cb = new Callback();
                var ret = p.opFloatDoubleAsync(3.14f, 1.1E10).Result;
                cb.opFloatDouble(ret.ReturnValue, ret.p3, ret.p4);
            }

            {
                var cb = new Callback();
                var ret = p.opStringAsync("hello", "world").Result;
                cb.opString(ret.ReturnValue, ret.p3);
            }

            {
                var cb = new Callback();
                var ret = p.opMyEnumAsync(Test.MyEnum.enum2).Result;
                cb.opMyEnum(ret.ReturnValue, ret.p2);
            }

            {
                var cb = new Callback(communicator);
                var ret = p.opMyClassAsync(p).Result;
                cb.opMyClass(ret.ReturnValue, ret.p2, ret.p3);
            }

            {
                var si1 = new Test.Structure();
                si1.p = p;
                si1.e = Test.MyEnum.enum3;
                si1.s = new Test.AnotherStruct();
                si1.s.s = "abc";
                var si2 = new Test.Structure();
                si2.p = null;
                si2.e = Test.MyEnum.enum2;
                si2.s = new Test.AnotherStruct();
                si2.s.s = "def";

                var cb = new Callback(communicator);
                var ret = p.opStructAsync(si1, si2).Result;
                cb.opStruct(ret.ReturnValue, ret.p3);
            }

            {
                byte[] bsi1 = new byte[] { 0x01, 0x11, 0x12, 0x22 };
                byte[] bsi2 = new byte[] { 0xf1, 0xf2, 0xf3, 0xf4 };

                var cb = new Callback();
                var ret = p.opByteSAsync(bsi1, bsi2).Result;
                cb.opByteS(ret.ReturnValue, ret.p3);
            }

            {
                bool[] bsi1 = new bool[] { true, true, false };
                bool[] bsi2 = new bool[] { false };

                var cb = new Callback();
                var ret = p.opBoolSAsync(bsi1, bsi2).Result;
                cb.opBoolS(ret.ReturnValue, ret.p3);
            }

            {
                short[] ssi = new short[] { 1, 2, 3 };
                int[] isi = new int[] { 5, 6, 7, 8 };
                long[] lsi = new long[] { 10, 30, 20 };

                var cb = new Callback();
                var ret = p.opShortIntLongSAsync(ssi, isi, lsi).Result;
                cb.opShortIntLongS(ret.ReturnValue, ret.p4, ret.p5, ret.p6);
            }

            {
                float[] fsi = new float[] { 3.14f, 1.11f };
                double[] dsi = new double[] { 1.1e10, 1.2e10, 1.3e10 };

                var cb = new Callback();
                var ret = p.opFloatDoubleSAsync(fsi, dsi).Result;
                cb.opFloatDoubleS(ret.ReturnValue, ret.p3, ret.p4);
            }

            {
                string[] ssi1 = new string[] { "abc", "de", "fghi" };
                string[] ssi2 = new string[] { "xyz" };

                var cb = new Callback();
                var ret = p.opStringSAsync(ssi1, ssi2).Result;
                cb.opStringS(ret.ReturnValue, ret.p3);
            }

            {
                byte[] s11 = new byte[] { 0x01, 0x11, 0x12 };
                byte[] s12 = new byte[] { 0xff };
                byte[][] bsi1 = new byte[][] { s11, s12 };

                byte[] s21 = new byte[] { 0x0e };
                byte[] s22 = new byte[] { 0xf2, 0xf1 };
                byte[][] bsi2 = new byte[][] { s21, s22 };

                var cb = new Callback();
                var ret = p.opByteSSAsync(bsi1, bsi2).Result;
                cb.opByteSS(ret.ReturnValue, ret.p3);
            }

            {
                bool[] s11 = new bool[] { true };
                bool[] s12 = new bool[] { false };
                bool[] s13 = new bool[] { true, true };
                bool[][] bsi1 = new bool[][] { s11, s12, s13 };

                bool[] s21 = new bool[] { false, false, true };
                bool[][] bsi2 = new bool[][] { s21 };

                var cb = new Callback();
                var ret = p.opBoolSSAsync(bsi1, bsi2).Result;
                cb.opBoolSS(ret.ReturnValue, ret.p3);
            }

            {
                short[] s11 = new short[] { 1, 2, 5 };
                short[] s12 = new short[] { 13 };
                short[] s13 = new short[] { };
                short[][] ssi = new short[][] { s11, s12, s13 };

                int[] i11 = new int[] { 24, 98 };
                int[] i12 = new int[] { 42 };
                int[][] isi = new int[][] { i11, i12 };

                long[] l11 = new long[] { 496, 1729 };
                long[][] lsi = new long[][] { l11 };

                var cb = new Callback();
                var ret = p.opShortIntLongSSAsync(ssi, isi, lsi).Result;
                cb.opShortIntLongSS(ret.ReturnValue, ret.p4, ret.p5, ret.p6);
            }

            {
                float[] f11 = new float[] { 3.14f };
                float[] f12 = new float[] { 1.11f };
                float[] f13 = new float[] { };
                float[][] fsi = new float[][] { f11, f12, f13 };

                double[] d11 = new double[] { 1.1e10, 1.2e10, 1.3e10 };
                double[][] dsi = new double[][] { d11 };

                var cb = new Callback();
                var ret = p.opFloatDoubleSSAsync(fsi, dsi).Result;
                cb.opFloatDoubleSS(ret.ReturnValue, ret.p3, ret.p4);
            }

            {
                string[] s11 = new string[] { "abc" };
                string[] s12 = new string[] { "de", "fghi" };
                string[][] ssi1 = new string[][] { s11, s12 };

                string[] s21 = new string[] { };
                string[] s22 = new string[] { };
                string[] s23 = new string[] { "xyz" };
                string[][] ssi2 = new string[][] { s21, s22, s23 };

                var cb = new Callback();
                var ret = p.opStringSSAsync(ssi1, ssi2).Result;
                cb.opStringSS(ret.ReturnValue, ret.p3);
            }

            {
                string[] s111 = new string[] { "abc", "de" };
                string[] s112 = new string[] { "xyz" };
                string[][] ss11 = new string[][] { s111, s112 };
                string[] s121 = new string[] { "hello" };
                string[][] ss12 = new string[][] { s121 };
                string[][][] sssi1 = new string[][][] { ss11, ss12 };

                string[] s211 = new string[] { "", "" };
                string[] s212 = new string[] { "abcd" };
                string[][] ss21 = new string[][] { s211, s212 };
                string[] s221 = new string[] { "" };
                string[][] ss22 = new string[][] { s221 };
                string[][] ss23 = new string[][] { };
                string[][][] sssi2 = new string[][][] { ss21, ss22, ss23 };

                var cb = new Callback();
                var ret = p.opStringSSSAsync(sssi1, sssi2).Result;
                cb.opStringSSS(ret.ReturnValue, ret.p3);
            }

            {
                Dictionary<byte, bool> di1 = new Dictionary<byte, bool>();
                di1[10] = true;
                di1[100] = false;
                Dictionary<byte, bool> di2 = new Dictionary<byte, bool>();
                di2[10] = true;
                di2[11] = false;
                di2[101] = true;

                var cb = new Callback();
                var ret = p.opByteBoolDAsync(di1, di2).Result;
                cb.opByteBoolD(ret.ReturnValue, ret.p3);
            }

            {
                Dictionary<short, int> di1 = new Dictionary<short, int>();
                di1[110] = -1;
                di1[1100] = 123123;
                Dictionary<short, int> di2 = new Dictionary<short, int>();
                di2[110] = -1;
                di2[111] = -100;
                di2[1101] = 0;

                var cb = new Callback();
                var ret = p.opShortIntDAsync(di1, di2).Result;
                cb.opShortIntD(ret.ReturnValue, ret.p3);
            }

            {
                Dictionary<long, float> di1 = new Dictionary<long, float>();
                di1[999999110L] = -1.1f;
                di1[999999111L] = 123123.2f;
                Dictionary<long, float> di2 = new Dictionary<long, float>();
                di2[999999110L] = -1.1f;
                di2[999999120L] = -100.4f;
                di2[999999130L] = 0.5f;

                var cb = new Callback();
                var ret = p.opLongFloatDAsync(di1, di2).Result;
                cb.opLongFloatD(ret.ReturnValue, ret.p3);
            }

            {
                Dictionary<string, string> di1 = new Dictionary<string, string>();
                di1["foo"] = "abc -1.1";
                di1["bar"] = "abc 123123.2";
                Dictionary<string, string> di2 = new Dictionary<string, string>();
                di2["foo"] = "abc -1.1";
                di2["FOO"] = "abc -100.4";
                di2["BAR"] = "abc 0.5";

                var cb = new Callback();
                var ret = p.opStringStringDAsync(di1, di2).Result;
                cb.opStringStringD(ret.ReturnValue, ret.p3);
            }

            {
                var di1 = new Dictionary<string, Test.MyEnum>();
                di1["abc"] = Test.MyEnum.enum1;
                di1[""] = Test.MyEnum.enum2;
                var di2 = new Dictionary<string, Test.MyEnum>();
                di2["abc"] = Test.MyEnum.enum1;
                di2["qwerty"] = Test.MyEnum.enum3;
                di2["Hello!!"] = Test.MyEnum.enum2;

                var cb = new Callback();
                var ret = p.opStringMyEnumDAsync(di1, di2).Result;
                cb.opStringMyEnumD(ret.ReturnValue, ret.p3);
            }

            {
                var di1 = new Dictionary<Test.MyEnum, string>();
                di1[Test.MyEnum.enum1] = "abc";
                var di2 = new Dictionary<Test.MyEnum, string>();
                di2[Test.MyEnum.enum2] = "Hello!!";
                di2[Test.MyEnum.enum3] = "qwerty";

                var cb = new Callback();
                var ret = p.opMyEnumStringDAsync(di1, di2).Result;
                cb.opMyEnumStringD(ret.ReturnValue, ret.p3);
            }

            {
                var s11 = new Test.MyStruct(1, 1);
                var s12 = new Test.MyStruct(1, 2);
                var di1 = new Dictionary<Test.MyStruct, Test.MyEnum>();
                di1[s11] = Test.MyEnum.enum1;
                di1[s12] = Test.MyEnum.enum2;

                var s22 = new Test.MyStruct(2, 2);
                var s23 = new Test.MyStruct(2, 3);
                var di2 = new Dictionary<Test.MyStruct, Test.MyEnum>();
                di2[s11] = Test.MyEnum.enum1;
                di2[s22] = Test.MyEnum.enum3;
                di2[s23] = Test.MyEnum.enum2;

                var cb = new Callback();
                var ret = p.opMyStructMyEnumDAsync(di1, di2).Result;
                cb.opMyStructMyEnumD(ret.ReturnValue, ret.p3);
            }

            {
                Dictionary<byte, bool>[] dsi1 = new Dictionary<byte, bool>[2];
                Dictionary<byte, bool>[] dsi2 = new Dictionary<byte, bool>[1];

                Dictionary<byte, bool> di1 = new Dictionary<byte, bool>();
                di1[10] = true;
                di1[100] = false;
                Dictionary<byte, bool> di2 = new Dictionary<byte, bool>();
                di2[10] = true;
                di2[11] = false;
                di2[101] = true;
                Dictionary<byte, bool> di3 = new Dictionary<byte, bool>();
                di3[100] = false;
                di3[101] = false;

                dsi1[0] = di1;
                dsi1[1] = di2;
                dsi2[0] = di3;

                var cb = new Callback();
                var ret = p.opByteBoolDSAsync(dsi1, dsi2).Result;
                cb.opByteBoolDS(ret.ReturnValue, ret.p3);
            }

            {
                Dictionary<long, float>[] dsi1 = new Dictionary<long, float>[2];
                Dictionary<long, float>[] dsi2 = new Dictionary<long, float>[1];

                Dictionary<long, float> di1 = new Dictionary<long, float>();
                di1[999999110L] = -1.1f;
                di1[999999111L] = 123123.2f;
                Dictionary<long, float> di2 = new Dictionary<long, float>();
                di2[999999110L] = -1.1f;
                di2[999999120L] = -100.4f;
                di2[999999130L] = 0.5f;
                Dictionary<long, float> di3 = new Dictionary<long, float>();
                di3[999999140L] = 3.14f;

                dsi1[0] = di1;
                dsi1[1] = di2;
                dsi2[0] = di3;

                var cb = new Callback();
                var ret = p.opLongFloatDSAsync(dsi1, dsi2).Result;
                cb.opLongFloatDS(ret.ReturnValue, ret.p3);
            }

            {
                Dictionary<string, string>[] dsi1 = new Dictionary<string, string>[2];
                Dictionary<string, string>[] dsi2 = new Dictionary<string, string>[1];

                Dictionary<string, string> di1 = new Dictionary<string, string>();
                di1["foo"] = "abc -1.1";
                di1["bar"] = "abc 123123.2";
                Dictionary<string, string> di2 = new Dictionary<string, string>();
                di2["foo"] = "abc -1.1";
                di2["FOO"] = "abc -100.4";
                di2["BAR"] = "abc 0.5";
                Dictionary<string, string> di3 = new Dictionary<string, string>();
                di3["f00"] = "ABC -3.14";

                dsi1[0] = di1;
                dsi1[1] = di2;
                dsi2[0] = di3;

                var cb = new Callback();
                var ret = p.opStringStringDSAsync(dsi1, dsi2).Result;
                cb.opStringStringDS(ret.ReturnValue, ret.p3);
            }

            {
                var dsi1 = new Dictionary<string, Test.MyEnum>[2];
                var dsi2 = new Dictionary<string, Test.MyEnum>[1];

                var di1 = new Dictionary<string, Test.MyEnum>();
                di1["abc"] = Test.MyEnum.enum1;
                di1[""] = Test.MyEnum.enum2;
                var di2 = new Dictionary<string, Test.MyEnum>();
                di2["abc"] = Test.MyEnum.enum1;
                di2["qwerty"] = Test.MyEnum.enum3;
                di2["Hello!!"] = Test.MyEnum.enum2;
                var di3 = new Dictionary<string, Test.MyEnum>();
                di3["Goodbye"] = Test.MyEnum.enum1;

                dsi1[0] = di1;
                dsi1[1] = di2;
                dsi2[0] = di3;

                var cb = new Callback();
                var ret = p.opStringMyEnumDSAsync(dsi1, dsi2).Result;
                cb.opStringMyEnumDS(ret.ReturnValue, ret.p3);
            }

            {
                var dsi1 = new Dictionary<Test.MyEnum, string>[2];
                var dsi2 = new Dictionary<Test.MyEnum, string>[1];

                var di1 = new Dictionary<Test.MyEnum, string>();
                di1[Test.MyEnum.enum1] = "abc";
                var di2 = new Dictionary<Test.MyEnum, string>();
                di2[Test.MyEnum.enum2] = "Hello!!";
                di2[Test.MyEnum.enum3] = "qwerty";
                var di3 = new Dictionary<Test.MyEnum, string>();
                di3[Test.MyEnum.enum1] = "Goodbye";

                dsi1[0] = di1;
                dsi1[1] = di2;
                dsi2[0] = di3;

                var cb = new Callback();
                var ret = p.opMyEnumStringDSAsync(dsi1, dsi2).Result;
                cb.opMyEnumStringDS(ret.ReturnValue, ret.p3);
            }

            {
                var dsi1 = new Dictionary<Test.MyStruct, Test.MyEnum>[2];
                var dsi2 = new Dictionary<Test.MyStruct, Test.MyEnum>[1];

                var s11 = new Test.MyStruct(1, 1);
                var s12 = new Test.MyStruct(1, 2);
                var di1 = new Dictionary<Test.MyStruct, Test.MyEnum>();
                di1[s11] = Test.MyEnum.enum1;
                di1[s12] = Test.MyEnum.enum2;

                var s22 = new Test.MyStruct(2, 2);
                var s23 = new Test.MyStruct(2, 3);
                var di2 = new Dictionary<Test.MyStruct, Test.MyEnum>();
                di2[s11] = Test.MyEnum.enum1;
                di2[s22] = Test.MyEnum.enum3;
                di2[s23] = Test.MyEnum.enum2;

                var di3 = new Dictionary<Test.MyStruct, Test.MyEnum>();
                di3[s23] = Test.MyEnum.enum3;

                dsi1[0] = di1;
                dsi1[1] = di2;
                dsi2[0] = di3;

                var cb = new Callback();
                var ret = p.opMyStructMyEnumDSAsync(dsi1, dsi2).Result;
                cb.opMyStructMyEnumDS(ret.ReturnValue, ret.p3);
            }

            {
                Dictionary<byte, byte[]> sdi1 = new Dictionary<byte, byte[]>();
                Dictionary<byte, byte[]> sdi2 = new Dictionary<byte, byte[]>();

                byte[] si1 = new byte[] { 0x01, 0x11 };
                byte[] si2 = new byte[] { 0x12 };
                byte[] si3 = new byte[] { 0xf2, 0xf3 };

                sdi1[0x01] = si1;
                sdi1[0x22] = si2;
                sdi2[0xf1] = si3;

                var cb = new Callback();
                var ret = p.opByteByteSDAsync(sdi1, sdi2).Result;
                cb.opByteByteSD(ret.ReturnValue, ret.p3);
            }

            {
                Dictionary<bool, bool[]> sdi1 = new Dictionary<bool, bool[]>();
                Dictionary<bool, bool[]> sdi2 = new Dictionary<bool, bool[]>();

                bool[] si1 = new bool[] { true, false };
                bool[] si2 = new bool[] { false, true, true };

                sdi1[false] = si1;
                sdi1[true] = si2;
                sdi2[false] = si1;

                var cb = new Callback();
                var ret = p.opBoolBoolSDAsync(sdi1, sdi2).Result;
                cb.opBoolBoolSD(ret.ReturnValue, ret.p3);
            }

            {
                Dictionary<short, short[]> sdi1 = new Dictionary<short, short[]>();
                Dictionary<short, short[]> sdi2 = new Dictionary<short, short[]>();

                short[] si1 = new short[] { 1, 2, 3 };
                short[] si2 = new short[] { 4, 5 };
                short[] si3 = new short[] { 6, 7 };

                sdi1[1] = si1;
                sdi1[2] = si2;
                sdi2[4] = si3;

                var cb = new Callback();
                var ret = p.opShortShortSDAsync(sdi1, sdi2).Result;
                cb.opShortShortSD(ret.ReturnValue, ret.p3);
            }

            {
                Dictionary<int, int[]> sdi1 = new Dictionary<int, int[]>();
                Dictionary<int, int[]> sdi2 = new Dictionary<int, int[]>();

                int[] si1 = new int[] { 100, 200, 300 };
                int[] si2 = new int[] { 400, 500 };
                int[] si3 = new int[] { 600, 700 };

                sdi1[100] = si1;
                sdi1[200] = si2;
                sdi2[400] = si3;

                var cb = new Callback();
                var ret = p.opIntIntSDAsync(sdi1, sdi2).Result;
                cb.opIntIntSD(ret.ReturnValue, ret.p3);
            }

            {
                Dictionary<long, long[]> sdi1 = new Dictionary<long, long[]>();
                Dictionary<long, long[]> sdi2 = new Dictionary<long, long[]>();

                long[] si1 = new long[] { 999999110L, 999999111L, 999999110L };
                long[] si2 = new long[] { 999999120L, 999999130L };
                long[] si3 = new long[] { 999999110L, 999999120L };

                sdi1[999999990L] = si1;
                sdi1[999999991L] = si2;
                sdi2[999999992L] = si3;

                var cb = new Callback();
                var ret = p.opLongLongSDAsync(sdi1, sdi2).Result;
                cb.opLongLongSD(ret.ReturnValue, ret.p3);
            }

            {
                Dictionary<string, float[]> sdi1 = new Dictionary<string, float[]>();
                Dictionary<string, float[]> sdi2 = new Dictionary<string, float[]>();

                float[] si1 = new float[] { -1.1f, 123123.2f, 100.0f };
                float[] si2 = new float[] { 42.24f, -1.61f };
                float[] si3 = new float[] { -3.14f, 3.14f };

                sdi1["abc"] = si1;
                sdi1["ABC"] = si2;
                sdi2["aBc"] = si3;

                var cb = new Callback();
                var ret = p.opStringFloatSDAsync(sdi1, sdi2).Result;
                cb.opStringFloatSD(ret.ReturnValue, ret.p3);
            }

            {
                Dictionary<string, double[]> sdi1 = new Dictionary<string, double[]>();
                Dictionary<string, double[]> sdi2 = new Dictionary<string, double[]>();

                double[] si1 = new double[] { 1.1E10, 1.2E10, 1.3E10 };
                double[] si2 = new double[] { 1.4E10, 1.5E10 };
                double[] si3 = new double[] { 1.6E10, 1.7E10 };

                sdi1["Hello!!"] = si1;
                sdi1["Goodbye"] = si2;
                sdi2[""] = si3;

                var cb = new Callback();
                var ret = p.opStringDoubleSDAsync(sdi1, sdi2).Result;
                cb.opStringDoubleSD(ret.ReturnValue, ret.p3);
            }

            {
                Dictionary<string, string[]> sdi1 = new Dictionary<string, string[]>();
                Dictionary<string, string[]> sdi2 = new Dictionary<string, string[]>();

                string[] si1 = new string[] { "abc", "de", "fghi" };
                string[] si2 = new string[] { "xyz", "or" };
                string[] si3 = new string[] { "and", "xor" };

                sdi1["abc"] = si1;
                sdi1["def"] = si2;
                sdi2["ghi"] = si3;

                var cb = new Callback();
                var ret = p.opStringStringSDAsync(sdi1, sdi2).Result;
                cb.opStringStringSD(ret.ReturnValue, ret.p3);
            }

            {
                var sdi1 = new Dictionary<Test.MyEnum, Test.MyEnum[]>();
                var sdi2 = new Dictionary<Test.MyEnum, Test.MyEnum[]>();

                var si1 = new Test.MyEnum[] { Test.MyEnum.enum1, Test.MyEnum.enum1, Test.MyEnum.enum2 };
                var si2 = new Test.MyEnum[] { Test.MyEnum.enum1, Test.MyEnum.enum2 };
                var si3 = new Test.MyEnum[] { Test.MyEnum.enum3, Test.MyEnum.enum3 };

                sdi1[Test.MyEnum.enum3] = si1;
                sdi1[Test.MyEnum.enum2] = si2;
                sdi2[Test.MyEnum.enum1] = si3;

                var cb = new Callback();
                var ret = p.opMyEnumMyEnumSDAsync(sdi1, sdi2).Result;
                cb.opMyEnumMyEnumSD(ret.ReturnValue, ret.p3);
            }

            {
                int[] lengths = new int[] { 0, 1, 2, 126, 127, 128, 129, 253, 254, 255, 256, 257, 1000 };

                for (int l = 0; l < lengths.Length; ++l)
                {
                    int[] s = new int[lengths[l]];
                    for (int i = 0; i < lengths[l]; ++i)
                    {
                        s[i] = i;
                    }

                    var cb = new Callback(lengths[l]);
                    cb.opIntS(p.opIntSAsync(s).Result);
                }
            }

            {
                Dictionary<string, string> ctx = new Dictionary<string, string>();
                ctx["one"] = "ONE";
                ctx["two"] = "TWO";
                ctx["three"] = "THREE";
                {
                    TestHelper.Assert(p.Context.Count == 0);
                    var cb = new Callback(ctx);
                    cb.opContextNotEqual(p.opContextAsync().Result);
                }
                {
                    TestHelper.Assert(p.Context.Count == 0);
                    var cb = new Callback(ctx);
                    cb.opContextEqual(p.opContextAsync(ctx).Result);
                }
                {
                    var p2 = p.Clone(context: ctx);
                    TestHelper.Assert(Collections.Equals(p2.Context, ctx));
                    var cb = new Callback(ctx);
                    cb.opContextEqual(p2.opContextAsync().Result);
                }
                {
                    var p2 = p.Clone(context: ctx);
                    Callback cb = new Callback(ctx);
                    cb.opContextEqual(p2.opContextAsync(ctx).Result);
                }
            }

            //
            // Test implicit context propagation with async task
            //
            if (p.GetConnection() != null)
            {
                communicator.CurrentContext["one"] = "ONE";
                communicator.CurrentContext["two"] = "TWO";
                communicator.CurrentContext["three"] = "THREE";

                var p3 = Test.IMyClassPrx.Parse($"test:{helper.GetTestEndpoint(0)}", communicator);
                TestHelper.Assert(Collections.Equals(p3.opContextAsync().Result, communicator.CurrentContext));

                Dictionary<string, string> prxContext = new Dictionary<string, string>();
                prxContext["one"] = "UN";
                prxContext["four"] = "QUATRE";

                Dictionary<string, string> combined = new Dictionary<string, string>(prxContext);
                foreach (KeyValuePair<string, string> e in communicator.CurrentContext)
                {
                    try
                    {
                        combined.Add(e.Key, e.Value);
                    }
                    catch (ArgumentException)
                    {
                        // Ignore.
                    }
                }
                TestHelper.Assert(combined["one"].Equals("UN"));

                TestHelper.Assert(communicator.DefaultContext.Count == 0);
                communicator.DefaultContext = prxContext;
                TestHelper.Assert(communicator.DefaultContext != prxContext); // it's a copy
                TestHelper.Assert(Collections.Equals(communicator.DefaultContext, prxContext));

                p3 = Test.IMyClassPrx.Parse($"test:{helper.GetTestEndpoint(0)}", communicator);

                var ctx = new Dictionary<string, string>(communicator.CurrentContext);
                communicator.CurrentContext.Clear();
                TestHelper.Assert(Collections.Equals(p3.opContextAsync().Result, prxContext));

                communicator.CurrentContext = ctx;
                TestHelper.Assert(Collections.Equals(p3.opContextAsync().Result, combined));

                // Cleanup
                communicator.CurrentContext.Clear();
                communicator.DefaultContext = new Dictionary<string, string>();
            }

            p.opIdempotentAsync().Wait();

            try
            {
                p.opOnewayAsync().Wait();
                TestHelper.Assert(false);
            }
            catch (System.AggregateException ex)
            {
                TestHelper.Assert(ex.InnerException is Test.SomeException);
            }

            {
                var derived = Test.IMyDerivedClassPrx.CheckedCast(p);
                TestHelper.Assert(derived != null);
                derived.opDerivedAsync().Wait();
            }

            {
                TestHelper.Assert(p.opByte1Async(0xFF).Result == 0xFF);
                TestHelper.Assert(p.opInt1Async(0x7FFFFFFF).Result == 0x7FFFFFFF);
                TestHelper.Assert(p.opLong1Async(0x7FFFFFFFFFFFFFFF).Result == 0x7FFFFFFFFFFFFFFF);
                TestHelper.Assert(p.opFloat1Async(1.0f).Result == 1.0f);
                TestHelper.Assert(p.opDouble1Async(1.0d).Result == 1.0d);
                TestHelper.Assert(p.opString1Async("opString1").Result.Equals("opString1"));
                TestHelper.Assert(p.opStringS1Async(Array.Empty<string>()).Result.Length == 0);
                TestHelper.Assert(p.opByteBoolD1Async(new Dictionary<byte, bool>()).Result.Count == 0);
                TestHelper.Assert(p.opStringS2Async(Array.Empty<string>()).Result.Length == 0);
                TestHelper.Assert(p.opByteBoolD2Async(new Dictionary<byte, bool>()).Result.Count == 0);
            }

            Func<Task> task = async () =>
            {
                {
                    var p1 = await p.opMStruct1Async();

                    p1.e = Test.MyEnum.enum3;
                    var r = await p.opMStruct2Async(p1);
                    TestHelper.Assert(r.p2.Equals(p1) && r.ReturnValue.Equals(p1));
                }

                {
                    await p.opMSeq1Async();

                    var p1 = new string[1];
                    p1[0] = "test";
                    var r = await p.opMSeq2Async(p1);
                    TestHelper.Assert(Collections.Equals(r.p2, p1) && Collections.Equals(r.ReturnValue, p1));
                }

                {
                    await p.opMDict1Async();

                    var p1 = new Dictionary<string, string>();
                    p1["test"] = "test";
                    var r = await p.opMDict2Async(p1);
                    TestHelper.Assert(Collections.Equals(r.p2, p1) && Collections.Equals(r.ReturnValue, p1));
                }
            };
        }
    }
}
