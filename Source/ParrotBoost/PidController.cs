using System;

namespace ParrotBoost;

internal sealed class PidController
{
    private readonly double _kp;
    private readonly double _ki;
    private readonly double _kd;
    private double _integral;
    private double _lastError;
    private DateTime _lastUpdate;

    public PidController(double kp, double ki, double kd)
    {
        _kp = kp;
        _ki = ki;
        _kd = kd;
        _lastUpdate = DateTime.UtcNow;
    }

    public double Compute(double target, double current)
    {
        var now = DateTime.UtcNow;
        var dt = (now - _lastUpdate).TotalSeconds;
        if (dt <= 0) return 0;

        var error = target - current;
        _integral += error * dt;
        var derivative = (error - _lastError) / dt;

        var output = (_kp * error) + (_ki * _integral) + (_kd * derivative);

        _lastError = error;
        _lastUpdate = now;

        return output;
    }

    public void Reset()
    {
        _integral = 0;
        _lastError = 0;
        _lastUpdate = DateTime.UtcNow;
    }
}
