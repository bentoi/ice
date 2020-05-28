//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

#pragma once

[[3.7]]

module Test
{

exception BaseException
{
}

exception InvalidPointException : BaseException
{
    int index;
}

exception InvalidLengthException : BaseException
{
    int length;
}

exception OtherException
{
    int x;
    int y;
    int z;
    bool b;
}

struct PointF
{
    float x;
    float y;
    float z;
}

struct PointD
{
    double x;
    double y;
    double z;
}

struct Point
{
    int x;
    int y;
}
sequence<Point> Points;

struct Polyline
{
    Points vertices;
}

struct Color
{
    int r;
    int g;
    int b;
    int a;
}

dictionary<int, Color> StringColorMap;

struct ColorPalette
{
    StringColorMap colors;
}

class Pen
{
    int thickness;
    Test::Color color;
}

struct Draw
{
    Test::Color backgroundColor;
    Test::Pen pen;
    bool shared;
}

}
