using System;

namespace XpetX;

/// <summary>宠物数值系统：饱食、快乐、精力（0-100，每秒按 -0.05 × DecaySpeed 衰减）。</summary>
public sealed class PetStats
{
    public float Hunger { get; private set; } = 80f;
    public float Happiness { get; private set; } = 80f;
    public float Energy { get; private set; } = 80f;

    /// <summary>数值衰减速度倍率（来自全局配置 decaySpeed）。</summary>
    public double DecaySpeed { get; set; } = 1.0;

    /// <summary>任一数值变化时触发（UI 订阅用于刷新显示）。</summary>
    public event Action? OnStatsChanged;

    /// <summary>每帧调用：数值衰减。</summary>
    public void Update(float deltaTime)
    {
        float rate = 0.05f * (float)DecaySpeed;
        Hunger = Clamp(Hunger - rate * deltaTime);
        Happiness = Clamp(Happiness - rate * deltaTime);
        Energy = Clamp(Energy - rate * deltaTime);
        OnStatsChanged?.Invoke();
    }

    public void Feed()
    {
        Hunger = Clamp(Hunger + 20f);
        OnStatsChanged?.Invoke();
    }

    public void Play()
    {
        Happiness = Clamp(Happiness + 15f);
        Energy = Clamp(Energy - 10f);
        OnStatsChanged?.Invoke();
    }

    /// <summary>进食：饱食 +25，好感按加成（直接喂 vs 地上捡）变化。</summary>
    public void Eat(float happinessGain)
    {
        Hunger = Clamp(Hunger + 25f);
        Happiness = Clamp(Happiness + happinessGain);
        OnStatsChanged?.Invoke();
    }

    public void Sleep()
    {
        Energy = Clamp(Energy + 25f);
        OnStatsChanged?.Invoke();
    }

    /// <summary>从存档恢复数值。</summary>
    internal void SetValues(float hunger, float happiness, float energy)
    {
        Hunger = Clamp(hunger);
        Happiness = Clamp(happiness);
        Energy = Clamp(energy);
        OnStatsChanged?.Invoke();
    }

    /// <summary>按秒数应用衰减（离线时间补偿用）。</summary>
    internal void ApplyDecay(float seconds)
    {
        float rate = 0.05f * (float)DecaySpeed;
        SetValues(Hunger - rate * seconds, Happiness - rate * seconds, Energy - rate * seconds);
    }

    private static float Clamp(float value)
    {
        return value < 0f ? 0f : (value > 100f ? 100f : value);
    }
}