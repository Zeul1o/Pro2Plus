namespace Pro2Plus;

/// <summary>
/// 每个输出会话独占一个实例，不在多个手柄槽位间共享。
/// 角速度无效时必须立刻停止；重力无效时不得把倾斜的手柄强行扭回某个合成静止姿态。
/// 只改写交给输出的副本，源帧的有效性标记、计数与时钟仍描述真实收到的帧。
/// </summary>
public sealed class MotionSampleResolver
{
    private bool hasAcceleration;
    private short accelX, accelY, accelZ;

    public bool HasAcceleration => hasAcceleration;

    public GamepadState Resolve(GamepadState? input)
    {
        input ??= GamepadState.Neutral();
        if (input.AccelValid)
        {
            accelX = input.AccelX;
            accelY = input.AccelY;
            accelZ = input.AccelZ;
            hasAcceleration = true;
        }

        if (input.AccelValid && input.GyroValid)
        {
            return input;
        }

        GamepadState resolved = input.Clone();
        if (!input.AccelValid)
        {
            resolved.AccelX = hasAcceleration ? accelX : (short)0;
            resolved.AccelY = hasAcceleration ? accelY : (short)0;
            resolved.AccelZ = hasAcceleration ? accelZ : (short)4096;
            resolved.AccelValid = true;
        }

        if (!input.GyroValid)
        {
            resolved.GyroX = 0;
            resolved.GyroY = 0;
            resolved.GyroZ = 0;
            resolved.GyroValid = false;
        }

        return resolved;
    }

    public void Reset()
    {
        hasAcceleration = false;
        accelX = 0;
        accelY = 0;
        accelZ = 0;
    }
}
