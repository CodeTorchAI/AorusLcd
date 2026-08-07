namespace AorusLcd.Core.Nvapi;

/// <summary>NVAPI <c>NV_I2C_SPEED</c> for the <c>i2cSpeedKhz</c> field: discrete bus speeds, not literal kHz. <see cref="Default"/> leaves the controller's current (unspecified) speed, at which the Aorus LCD intermittently rejects writes; GCC pins the panel bus to <see cref="Khz400"/>.</summary>
public enum NvApiI2cSpeed : uint
{
    Default = 0,
    Khz3 = 1,
    Khz10 = 2,
    Khz33 = 3,
    Khz100 = 4,
    Khz200 = 5,
    Khz400 = 6,
}
