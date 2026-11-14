using System;
using System.Collections.Generic;
using Box2DX.Common;
using Box2DX.Dynamics;
using Box2DX.Collision;
using Math = System.Math;

namespace DesktopAngry
{
    internal class PhysHit
    {
        internal object A, B;
        internal float Imp;
        internal float VRel;
        internal Vec2 Normal;
        internal Vec2 At;
    }

    internal class Phys
    {
        internal const float SC = 76f;
        internal static World W;
        internal static double Hpx = 1440;
        internal static float Acc;
        internal static List<PhysHit> Raw = new List<PhysHit>();
        internal static List<PhysHit> Hits = new List<PhysHit>();
        internal static Dictionary<Body, object> Tags = new Dictionary<Body, object>();
        internal static int CbCount;
        internal static int NullBody;
        internal static string Err;
        internal static PhysListener Lst;

        internal static void Tag(Body b, object t)
        {
            if (b == null) return;
            try { b.SetUserData(t); } catch { }
        }

        internal static object TagOf(Body b)
        {
            if (b == null) return null;
            try { return b.GetUserData(); }
            catch { return null; }
        }

        internal static void Reset(double wpx, double hpx)
        {
            Hpx = hpx;
            float wm = (float)(wpx / SC);
            float hm = (float)(hpx / SC);
            AABB ab = new AABB();
            ab.LowerBound = new Vec2(-40f, -20f);
            ab.UpperBound = new Vec2(wm + 40f, hm + 20f);
            W = new World(ab, new Vec2(0f, -13f), true);
            Lst = new PhysListener();
            W.SetContactListener(Lst);
            Raw.Clear();
            Hits.Clear();
            Tags.Clear();
            CbCount = 0;
            Acc = 0f;

            float gy = (float)((hpx - 1130.0) / SC);
            AddStatic(wm / 2f, gy - 1.2f, wm / 2f + 40f, 1.2f, 0.75f);
            AddStatic(-2f, hm / 2f, 1f, hm / 2f + 20f, 0.40f);
            AddStatic(wm + 2f, hm / 2f, 1f, hm / 2f + 20f, 0.40f);
        }

        private static void AddStatic(float wx, float wy, float hw, float hh, float fric)
        {
            BodyDef bd = new BodyDef();
            bd.Position = new Vec2(wx, wy);
            bd.FixedRotation = true;
            Body b = W.CreateBody(bd);
            PolygonDef pd = new PolygonDef();
            pd.SetAsBox(hw, hh);
            pd.Friction = fric;
            pd.Restitution = 0f;
            pd.UserData = "static";
            b.CreateShape(pd);
            MassData mz = new MassData();
            mz.Mass = 0f;
            mz.I = 0f;
            mz.Center = new Vec2(0f, 0f);
            b.SetMass(mz);
        }

        internal static Body AddBox(double cxPx, double cyPx, double wPx, double hPx, float dens, float fric, object tag)
        {
            BodyDef bd = new BodyDef();
            bd.Position = new Vec2((float)(cxPx / SC), (float)((Hpx - cyPx) / SC));
            bd.UserData = tag;
            Body b = W.CreateBody(bd);
            PolygonDef pd = new PolygonDef();
            pd.SetAsBox((float)(wPx / 2.0 / SC), (float)(hPx / 2.0 / SC));
            pd.Density = dens;
            pd.Friction = fric;
            pd.Restitution = 0.02f;
            pd.UserData = tag;
            b.CreateShape(pd);
            b.SetMassFromShapes();
            Tag(b, tag);
            return b;
        }

        internal static Body AddCircle(double cxPx, double cyPx, double rPx, float dens, bool bullet, object tag)
        {
            BodyDef bd = new BodyDef();
            bd.Position = new Vec2((float)(cxPx / SC), (float)((Hpx - cyPx) / SC));
            bd.IsBullet = bullet;
            bd.UserData = tag;
            Body b = W.CreateBody(bd);
            CircleDef cd = new CircleDef();
            cd.Radius = (float)(rPx / SC);
            cd.Density = dens;
            cd.Friction = 0.35f;
            cd.Restitution = 0.06f;
            cd.UserData = tag;
            b.CreateShape(cd);
            b.SetMassFromShapes();
            Tag(b, tag);
            return b;
        }

        internal static void Destroy(object body)
        {
            Body b = body as Body;
            if (b == null || W == null) return;
            Tags.Remove(b);
            try { W.DestroyBody(b); }
            catch { }
        }

        internal static void Step(float dt)
        {
            if (W == null) return;
            Hits.Clear();
            Acc += dt;
            if (Acc > 0.2f) Acc = 0.2f;
            int guard = 0;
            while (Acc >= 1f / 60f && guard < 4)
            {
                Raw.Clear();
                W.Step(1f / 60f, 10, 8);
                Merge();
                Acc -= 1f / 60f;
                guard++;
            }
            if (guard >= 4) Acc = 0f;
        }

        private static void Merge()
        {
            for (int i = 0; i < Raw.Count; i++)
            {
                PhysHit h = Raw[i];
                bool done = false;
                for (int j = 0; j < Hits.Count; j++)
                {
                    PhysHit g = Hits[j];
                    if ((g.A == h.A && g.B == h.B) || (g.A == h.B && g.B == h.A))
                    {
                        if (h.VRel > g.VRel) g.VRel = h.VRel;
                        if (h.Imp > g.Imp) g.Imp = h.Imp;
                        done = true;
                        break;
                    }
                }
                if (!done) Hits.Add(h);
            }
        }

        internal static Vec2 PosOf(object body)
        {
            Body b = body as Body;
            if (b == null) return new Vec2(0f, 0f);
            return b.GetPosition();
        }

        internal static float AngOf(object body)
        {
            Body b = body as Body;
            if (b == null) return 0f;
            return b.GetAngle();
        }

        internal static void SetVel(object body, double vxPx, double vyPx)
        {
            Body b = body as Body;
            if (b == null) return;
            b.SetLinearVelocity(new Vec2((float)(vxPx / SC), (float)(-vyPx / SC)));
        }

        internal static void Boost(object body, double addPx)
        {
            Body b = body as Body;
            if (b == null) return;
            Vec2 v = b.GetLinearVelocity();
            float len = (float)Math.Sqrt(v.X * v.X + v.Y * v.Y);
            if (len < 0.01f) return;
            float add = (float)(addPx / SC);
            b.SetLinearVelocity(new Vec2(v.X + v.X / len * add, v.Y + v.Y / len * add));
        }

        // 爆炸冲击：朝 (dxPx,dyPx) 方向推一把，屏幕坐标方向（y 向下为正）
        internal static void Kick(object body, double dxPx, double dyPx, double powerPx)
        {
            Body b = body as Body;
            if (b == null) return;
            double len = Math.Sqrt(dxPx * dxPx + dyPx * dyPx);
            double ux, uy;
            if (len < 0.5) { ux = 0; uy = -1; }
            else { ux = dxPx / len; uy = dyPx / len; }
            Vec2 v = b.GetLinearVelocity();
            float mvx = (float)(ux * powerPx / SC);
            float mvy = (float)(-uy * powerPx / SC);
            b.SetLinearVelocity(new Vec2(v.X + mvx, v.Y + mvy));
        }

        internal static double SpeedPx(object body)
        {
            Body b = body as Body;
            if (b == null) return 0;
            Vec2 v = b.GetLinearVelocity();
            return Math.Sqrt(v.X * v.X + v.Y * v.Y) * SC;
        }

        internal static double SX(float wx) { return wx * SC; }
        internal static double SY(float wy) { return Hpx - wy * SC; }
        internal static double AngleDeg(float rad) { return -rad * 180.0 / Math.PI; }
    }

    internal class PhysListener : ContactListener
    {
        public override void Add(ContactPoint p) { }
        public override void Persist(ContactPoint p) { }
        public override void Remove(ContactPoint p) { }

        public override void Result(ContactResult r)
        {
            object a = null;
            object b = null;
            float vr = 0f;
            Phys.CbCount++;
            try
            {
                Body b1 = r.Shape1.GetBody();
                Body b2 = r.Shape2.GetBody();
                if (b1 == null || b2 == null) { Phys.NullBody++; return; }
                a = Phys.TagOf(b1);
                b = Phys.TagOf(b2);
                Vec2 v1 = b1.GetLinearVelocity();
                Vec2 v2 = b2.GetLinearVelocity();
                float dx = v1.X - v2.X, dy = v1.Y - v2.Y;
                vr = (float)Math.Sqrt(dx * dx + dy * dy);
            }
            catch (Exception ex)
            {
                if (Phys.Err == null) Phys.Err = ex.GetType().Name + ":" + ex.Message;
                return;
            }

            Phys.Raw.Add(new PhysHit
            {
                A = a,
                B = b,
                Imp = r.NormalImpulse,
                VRel = vr,
                Normal = r.Normal,
                At = r.Position
            });
        }
    }
}
