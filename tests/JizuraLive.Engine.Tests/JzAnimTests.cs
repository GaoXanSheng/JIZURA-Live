using System.Threading;
using System.Windows.Controls;
using System.Windows.Media;
using JizuraLive.App;
using Xunit;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// JzAnim 进入动画的红线：RenderTransform 的基值必须是终值（恒等/位移 0）。
/// 回归背景：PopIn/FadeSlideIn 曾把 Transform 基值设成动画起点（Scale 0.985 / Translate dy），
/// FillBehavior.Stop 播完回落基值 → 卡片永久停在 0.985 缩放（RenderTransform 缩放是重采样，
/// 全篇文字发虚）且元素永久下移 4-6px。契约见 JzAnim 文件头第 3 条。
/// </summary>
public class JzAnimTests
{
    [Fact]
    public void 进入动画的变换基值就是终值()
    {
        Exception? err = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (!JzAnim.Enabled) return; // 系统关动画：函数直接返回不设变换，无可断言

                // FadeSlideIn：基值 TranslateY 必须是 0（此前为 dy=4 → 元素永久下移）
                var el1 = new Border();
                JzAnim.FadeSlideIn(el1);
                var tt1 = Assert.IsType<TranslateTransform>(el1.RenderTransform);
                tt1.BeginAnimation(TranslateTransform.YProperty, null); // 清动画暴露基值
                Assert.Equal(0, tt1.Y);

                // PopIn：基值必须 Scale=1 / TranslateY=0（此前 0.985/6 → 卡片常驻缩放糊字）
                var el2 = new Border();
                JzAnim.PopIn(el2);
                var tg = Assert.IsType<TransformGroup>(el2.RenderTransform);
                var sc = tg.Children.OfType<ScaleTransform>().Single();
                var tt2 = tg.Children.OfType<TranslateTransform>().Single();
                sc.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                sc.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                tt2.BeginAnimation(TranslateTransform.YProperty, null);
                Assert.Equal(1, sc.ScaleX);
                Assert.Equal(1, sc.ScaleY);
                Assert.Equal(0, tt2.Y);
            }
            catch (Exception e)
            {
                err = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA); // WPF 对象的操作性断言走 STA 线程
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "STA 测试线程超时");
        if (err != null) throw new Xunit.Sdk.XunitException(err.ToString());
    }
}
