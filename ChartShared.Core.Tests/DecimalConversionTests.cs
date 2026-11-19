namespace ChartShared.Core.Tests;

/// <summary>A double becomes the decimal a person would write for it, not its exact binary value.</summary>
[TestClass]
public sealed class DecimalConversionTests
{
    [TestMethod]
    public void AFractionADoubleCannotHoldExactlyComesOutAsWritten()
    {
        Assert.AreEqual(0.1m, DecimalConversion.ToDecimal(0.1));
        Assert.AreEqual(120.3m, DecimalConversion.ToDecimal(120.3));
        Assert.AreEqual(-123.4567m, DecimalConversion.ToDecimal(-123.4567));
    }

    [TestMethod]
    public void ArithmeticNoiseBeyondFifteenDigitsIsDropped()
    {
        Assert.AreEqual(0.3m, DecimalConversion.ToDecimal(0.1 + 0.2));
        Assert.AreEqual(0.3m, DecimalConversion.ToDecimal(3 * 0.1));
    }

    [TestMethod]
    public void WholeAndVerySmallOrLargeNumbersConvert()
    {
        Assert.AreEqual(0m, DecimalConversion.ToDecimal(0));
        Assert.AreEqual(250m, DecimalConversion.ToDecimal(250));
        Assert.AreEqual(0.000000125m, DecimalConversion.ToDecimal(1.25e-7));
        Assert.AreEqual(12_500_000_000_000_000_000m, DecimalConversion.ToDecimal(1.25e19));
    }

    [TestMethod]
    public void WhatADecimalCannotHoldOverflows()
    {
        Assert.ThrowsExactly<OverflowException>(() => DecimalConversion.ToDecimal(double.NaN));
        Assert.ThrowsExactly<OverflowException>(() => DecimalConversion.ToDecimal(double.PositiveInfinity));
        Assert.ThrowsExactly<OverflowException>(() => DecimalConversion.ToDecimal(1e30));
    }
}
