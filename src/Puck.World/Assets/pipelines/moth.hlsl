// MOTH - procedural SDF rendering of 01-character-model-sheet.png.
// Swept shoulders, curved boots and surface finish: 02-face-and-armor.png.
// The avatars/moth world runs this file as the one-off moth-pipeline source; pipeline.watch moth-pipeline on reloads saved edits.
// Reference sheets: docs/game/art/moth-concept-pack/.
// The shader projects through its paired camera when supplied, exactly as the camera does; otherwise the orbit below.
// Drag mouse: orbit. Release: hold view. Set AUTO_TURN to 1 for a turntable.
// Approximate sculpt, not a mesh reconstruction. Front is -Z; positions and distances use world units.
// CLOSE_UP: face/shoulder framing. AA: 1 = fast, 2 = four samples per pixel.
// POSE: 0 rest, 1 jump prep, 2 takeoff, 3 hover, 4 flight, 5 brake, 6 land, 7 run.
// ANIMATE_POSE loops rest / jump / hover / flight / brake / land using the same rig.
// ANIMATE_FACE adds a brief blink every 5.1 seconds; set to 0 for a still image.
// Plates use rigid joint transforms; surface detail follows each piece in every pose.
// Graphic amber eyes, sculpted facial planes, swept hair and satin pearl paint.
// Secondary rays use simplified head geometry; eyelid occlusion is shaded locally.
// AA samples also sample the studio light; tone mapping follows linear averaging.
// PACK_VIEW frames the flight system. JETS toggles depth-clipped volumetric exhaust.
// ANIMATE_PACK adds a small hinge settle. Opening follows the selected pose.
// PACK_DEPLOY: -1.0 follows pose; 0.0..1.0 overrides opening (at most 6 degrees).
// OWNER_COLORWAY: 1 ivory shin shells from the owner crop, 0 lilac from sheet 1.
// 05-compact-wing-views.png anchors the twin shells and recessed lower nozzles.
// 06-compact-wing-motion.png anchors the pose sequence and restrained deployment.
// WEAR: 0 = fresh paint, 1 = restrained edge chips, scratches and contact abrasion.
// Jet flow animates with frameGroup.time; no noise textures, buffers or opaque flame meshes.
// The braid stays rooted beside the right cheek through the front helmet opening.
// RELAXED_TRACE checks overlapping empty-space bounds and retries rejected steps.
// Skin transmission and neighbor-color bounce are bounded shading approximations.
// GEOMETRIC_SEAMS cuts the pack's main reveals; fine engravings remain surface detail.
// CEL_STYLE: 0 = studio shading, 1 = stepped diffuse. INK adds a narrow outer contour.
#define AUTO_TURN 0
#define CLOSE_UP 0
#define PACK_VIEW 0
#define ISOLATE_PACK 1
#define JETS 1
#define PACK_DEPLOY -1.0
#define ANIMATE_PACK 1
#define OWNER_COLORWAY 1
#define WEAR 1
#define POSE 0
#define ANIMATE_POSE 0
#define ANIMATE_FACE 1
#define RELAXED_TRACE 1
#define GEOMETRIC_SEAMS 1
#define CEL_STYLE 0
#define INK 0
#define AA 2
#define MAX_STEPS 220
#define SHADOW_STEPS 96

// The generated interface declares the frame group, the pass block and the output image, 'output'.
#include "moth.interface.hlsli"

// The output image's size in pixels.
static float2 resolution;
// The width over height of the rect the image is placed in, which the paired camera projects at. A pane keeps its
// allocation's extent while its rect eases, so the image's own aspect is not the one the display shows.
static float aspect;

static const float PI = 3.14159265;
// Base material IDs. Hits add 20 times the rigid part index for local surface details.
static const float LILAC=1., IVORY=2., JOINT=3., SKIN=4., HAIR=5.;
static const float GOLD=6., CYAN=7., EYE=8., STEEL=9.;
static const float OCHRE=11.;
static const float LIP=12., MOUTH=13.;
static float eyeOpen;
struct MotionPose {
    float pitch, lift;
    float2 hip, knee, ankle, arm, elbow;
    float opening, thrust, trail, grip;
};
static MotionPose motion;
static float characterLift, packOpening;

// Applied as mul(v, rot(a)); the rows are (c, -s) and (s, c).
float2x2 rot(float a) { float c=cos(a),s=sin(a); return float2x2(c,-s,s,c); }
// Signed values need multiplication: pow with a negative base is undefined
// and can become NaN on the GPU (notably blinks and lip cuts).
float square(float x) { return x*x; }
// Floored modulo, whose result takes the divisor's sign; fmod truncates instead.
float floorMod(float x, float y) { return x-y*floor(x/y); }
// The inverse of a general 3x3 matrix, from the cofactors of its rows.
float3x3 inverse3(float3x3 m) {
    float3 a=m[0],b=m[1],c=m[2];
    float3x3 adjugate=float3x3(cross(b,c),cross(c,a),cross(a,b));
    return transpose(adjugate)/dot(a,cross(b,c));
}
float ell(float3 p, float3 r) {
    // Conservative distance bound, including at the ellipsoid center.
    return (length(p/r)-1.)*min(r.x,min(r.y,r.z));
}
float box(float3 p, float3 b, float r) {
    float3 q=abs(p)-b;
    return length(max(q,0.))+min(max(q.x,max(q.y,q.z)),0.)-r;
}
float cap(float3 p, float3 a, float3 b, float r) {
    float3 v=p-a,w=b-a;
    return length(v-w*clamp(dot(v,w)/dot(w,w),0.,1.))-r;
}
float cylZ(float3 p, float r, float h) {
    float2 d=float2(length(p.xy)-r,abs(p.z)-h);
    return min(max(d.x,d.y),0.)+length(max(d,0.));
}
float cylX(float3 p, float r, float h) { return cylZ(p.zyx,r,h); }
void add(inout float2 h, float d, float m) { if(d<h.x) h=float2(d,m); }
float smoothUnion(float a,float b,float k) {
    float t=clamp(.5+.5*(b-a)/k,0.,1.);
    return lerp(b,a,t)-k*t*(1.-t);
}
float smoothIntersection(float a,float b,float k) {
    return -smoothUnion(-a,-b,k);
}
float3 bezier(float3 a,float3 b,float3 c,float t) {
    return lerp(lerp(a,b,t),lerp(b,c,t),t);
}
static float4 hairNodes[52];
void prepareHair() {
    // Sample the authored curves once per pixel; march only the cached sweeps.
    for(int lock=0;lock<4;lock++) {
        float3 a,b,c; float width;
        if(lock==0) { a=float3(-0.0525,2.09,-0.1425); b=float3(0.0525,2.085,-0.285); c=float3(0.22,1.9,-0.18); width=0.0665; }
        else if(lock==1) { a=float3(-0.045,2.09,-0.143); b=float3(-0.145,2.055,-0.265); c=float3(-0.225,1.94,-0.165); width=0.044; }
        else if(lock==2) { a=float3(0.165,1.9625,-0.1425); b=float3(0.2175,1.86,-0.195); c=float3(0.1825,1.775,-0.1625); width=0.037; }
        else { a=float3(-0.1775,1.965,-0.115); b=float3(-0.2125,1.88,-0.1575); c=float3(-0.174,1.8,-0.13); width=0.0335; }
        for(int i=0;i<13;i++) {
            float t=float(i)/12.;
            // Broad roots join the hair cap, then taper into swept, thin tips.
            float taper=(.80+.40*sin(PI*t))*(1.-smoothstep(.35,1.,t));
            hairNodes[lock*13+i]=float4(bezier(a,b,c,t)*float3(1,1,3.1),0.004+width*taper);
        }
    }
}
float hairSculpt(float3 p) {
    float bound=box(p-float3(0,1.97,-0.19),float3(0.285,0.24,0.115),0.);
    if(bound>0.03) return bound;
    p*=float3(1,1,3.1);
    float d=5.;
    for(int i=0;i<48;i++) {
        int node=i+i/12;
        float4 a=hairNodes[node],b=hairNodes[node+1];
        float3 v=b.xyz-a.xyz;
        float t=clamp(dot(p-a.xyz,v)/dot(v,v),0.,1.);
        float strand=length(p-lerp(a.xyz,b.xyz,t))-lerp(a.w,b.w,t);
        d=smoothUnion(d,strand,0.007);
    }
    return d*.27;
}
float3 eyeCoordinates(float3 p) {
    p.x=-abs(p.x); p-=float3(-0.087,1.8715,-0.2105);
    p.xz=mul(p.xz,rot(-.21)); p.xy=mul(p.xy,rot(-.055));
    return p;
}
static const float3 EYE_RADII=float3(0.0725,0.0725,0.049);
float eyeFront(float2 p) { return -0.049*sqrt(max(1.-dot(p,p)/(0.0725*0.0725),.0001)); }
float2 eyelidHeights(float x) {
    float u=-x/0.055,arch=pow(max(1.-u*u,0.),.65);
    return float2(0.0415*arch+0.006*u,-0.0355*arch+0.006*u)*eyeOpen;
}
float eyeOpening(float3 q) {
    float2 lids=eyelidHeights(q.x);
    return max(max(q.y-lids.x,lids.y-q.y),abs(q.x)-0.055)*.60;
}
float lidDistance(float3 q,bool upper) {
    float x=clamp(q.x,-0.054,0.054);
    float2 heights=eyelidHeights(x);
    float y=upper?heights.x:heights.y;
    float z=eyeFront(float2(x,y));
    float radius=upper?(0.005+0.0025*smoothstep(-0.035,0.06,-x)):0.00175;
    return (length(q-float3(x,y,z))-radius)*.30;
}
static const float3 FACE_CENTER=float3(0,1.8625,-0.1175);
static const float3 FACE_RADII=float3(0.2,0.1875,0.1475);
float faceWidth(float y) {
    float jaw=lerp(.84,1.,smoothstep(1.69,1.825,y));
    return 0.2*jaw*(1.-.045*smoothstep(1.95,2.05,y));
}
float smileHeight(float x) { return 1.748+3.5*x*x-.018*x; }
float faceOffset(float2 p) {
    // A small lip volume on a broad facial surface; cheeks are shaped by planes.
    float muzzle=(1.-smoothstep(0.04,0.105,abs(p.x)))
                 *smoothstep(1.71,1.745,p.y)*(1.-smoothstep(1.765,1.8,p.y));
    float2 lip=float2(p.x/0.0525,(p.y-smileHeight(clamp(p.x,-0.065,0.065))+0.007)/0.006);
    return 0.005*muzzle+0.002*exp(-dot(lip,lip));
}
float faceFront(float2 p) {
    float2 q=float2(p.x/faceWidth(p.y),(p.y-FACE_CENTER.y)/FACE_RADII.y);
    return FACE_CENTER.z-faceOffset(p)-FACE_RADII.z*sqrt(max(1.-dot(q,q),0.));
}
float faceSculpt(float3 p) {
    // Broad cheek planes narrow into a rounded mandible, without added cheek balls.
    float3 q=p-FACE_CENTER; q.z+=faceOffset(p.xy);
    float d=ell(q,float3(faceWidth(p.y),FACE_RADII.yz));
    float3 s=p; s.x=-abs(s.x);
    float jaw=-.68*s.x-.70*(p.y-1.7)+-.18*(p.z+0.12)-0.068;
    d=smoothIntersection(d,jaw,0.0275);
    d=smoothIntersection(d,1.6835-p.y,0.01);
    d=smoothUnion(d,cap(p,float3(0,1.855,-0.252),float3(0,1.818,-0.2645),0.005),0.014);
    d=smoothUnion(d,ell(p-float3(0,1.805,-0.274),float3(0.018,0.012,0.017)),0.012);
    d=smoothUnion(d,ell(s-float3(-0.013,1.8005,-0.267),float3(0.0085,0.0065,0.009)),0.0085);
    return d*.70;
}
float skinGeometry(float3 p) {
    // A recessed throat widens into the jaw and shoulder root. Keep the front
    // behind the chin so the mandible has an underside instead of a skin stalk.
    float waist=exp(-square((p.y-1.64)/0.0525));
    float base=1.-smoothstep(1.57,1.635,p.y);
    float2 radius=float2(0.0755-0.0095*waist+0.018*base,0.052+0.011*base);
    float2 section=float2(p.x,p.z+0.0625)/radius;
    float neck=(length(section)-1.)*radius.y;
    neck=smoothIntersection(neck,max(1.56-p.y,p.y-1.745),0.0125)*.60;
    float skin=smoothUnion(faceSculpt(p),neck,0.008);
    float3 ear=p; ear.x=-abs(ear.x);
    return min(skin,ell(ear-float3(-0.1825,1.8225,-0.11),float3(0.024,0.045,0.0275)));
}
float edgePlane(float2 p,float2 a,float2 b) {
    float2 e=b-a;
    return dot(p-a,float2(e.y,-e.x))/length(e);
}
float fiveSides(float2 p,float2 a,float2 b,float2 c,float2 d,float2 e) {
    // Counterclockwise convex profile; half-plane distances are conservative at corners.
    float f=smoothIntersection(edgePlane(p,a,b),edgePlane(p,b,c),0.009);
    f=smoothIntersection(f,edgePlane(p,c,d),0.009);
    f=smoothIntersection(f,edgePlane(p,d,e),0.009);
    return smoothIntersection(f,edgePlane(p,e,a),0.009);
}
float roundedExtrusion(float profile,float depth,float bevel) {
    // Quarter-circle rounding in the profile/depth plane preserves the broad faces.
    float2 q=float2(profile,depth)+bevel;
    return min(max(q.x,q.y),0.)+length(max(q,0.))-bevel;
}
float chamferIntersection(float a,float b,float width) {
    // Plane/section intersection with a straight bevel; positive width cuts inward.
    return max(max(a,b),(a+b+width)*.70710678);
}
float superEllipse(float2 p,float2 r,float power) {
    float2 q=pow(abs(p)/r,(float2)power);
    return (pow(q.x+q.y,1./power)-1.)*min(r.x,r.y);
}
float carvePanel(float plate,float seam,float width,float depth,float bevel) {
    // Subtract a rounded trench extending from the surface down to the chosen depth.
    float2 q=float2(abs(seam)-width,-plate-depth)+bevel;
    float trench=length(max(q,0.))+min(max(q.x,q.y),0.)-bevel;
    return max(plate,-trench);
}
float shoulderProfile(float2 p) {
    return fiveSides(p,float2(0.06,0.05),float2(-0.04,0.1),float2(-0.145,0.05),
                     float2(-0.24,-0.225),float2(-0.06,-0.12));
}
float shoulderPlate(float3 q) {
    // Preserve a continuous outer wall and a rounded rim around the blade.
    // The cavity opens only toward the arm, on the hidden medial side.
    float radius=0.155*(1.-.50*smoothstep(0.025,0.225,-q.y));
    float section=superEllipse(float2(q.x-0.01,q.z),float2(0.25,radius),2.6);
    float outer=roundedExtrusion(shoulderProfile(q.xy),section,0.009);
    float inside=superEllipse(float2(q.x-0.01,q.z),float2(0.2325,radius-0.018),2.6);
    float cavityProfile=min(shoulderProfile(q.xy)+0.019,-q.x+0.0175);
    inside=max(inside,max(cavityProfile,q.y-0.036));
    return max(outer,-inside)*.65;
}
float2 shinRadii(float y) {
    float flare=clamp((0.705-y)/0.56,0.,1.);
    return float2(0.115,0.105)+float2(0.0775,0.115)*flare
         +float2(0.0175,0.02)*sin(PI*flare);
}
float curvedSection(float2 p,float2 r) {
    // Between an ellipse and a rounded rectangle: broad, gently curved plate faces.
    return superEllipse(p,r,2.6);
}
float shinSection(float3 q,float inset) {
    float flare=clamp((0.705-q.y)/0.56,0.,1.);
    // Round at the knee, with tensioned broad faces at the flared ankle.
    float power=lerp(2.15,3.65,flare*flare);
    return superEllipse(q.xz,shinRadii(q.y)-inset,power);
}
float ankleOpening(float2 p) {
    float arch=(length(float2(p.x,p.y-0.135)/float2(0.151,0.15))-1.)*0.15;
    return max(0.1425-p.y,-arch);
}
float footOutline(float3 q) {
    float outline=curvedSection(float2(q.x,q.z+0.085),float2(0.165,0.24));
    float toeCorners=(abs(q.x)+-.50*(q.z+0.24)-0.15)/1.118;
    return smoothIntersection(outline,toeCorners,0.009);
}
float3 gauntletCoordinates(float3 p) {
    p-=float3(-0.46,1.05,-0.0075); p.xy=mul(p.xy,rot(-.23));
    return p;
}
float gauntletShell(float3 q) {
    // A bowed exterior with a high elbow point and a rounded wrist cheek.
    float3 a=q-float3(-0.0425,0.0075,-0.0075);
    a.x-=.14*a.y+0.36*a.y*a.y;
    float shell=ell(a,float3(0.1325,0.2125,0.1375))*.78;
    float bevel=max(-.8*a.x+.15*a.y+.58*abs(a.z)-0.1225,
                    max(-a.y-.28*a.x-0.185,.9*a.y-.25*a.x-0.17));
    shell=smoothIntersection(shell,bevel,0.012);
    shell=smoothIntersection(shell,abs(a.z)-(0.107+.10*a.y),0.008);
    return smoothIntersection(shell,q.x-0.0175-.25*q.y,0.0125);
}
float3 nozzlePosition() { return float3(-0.1125,1.005,0.205); }
float3 nozzleCoordinates(float3 p) {
    p-=nozzlePosition();
    float cant=lerp(.48,.12,smoothstep(0.,.3,motion.thrust));
    cant-=.28*smoothstep(.02,.16,motion.pitch);
    p.xy=mul(p.xy,rot(.18)); p.yz=mul(p.yz,rot(-cant));
    return p;
}
float podBandCoordinate(float3 p) {
    float xRel=max(-p.x-0.019,0.);
    return p.y+0.19*pow(xRel*2.,1.15);
}
float podBandEdges(float3 p) {
    float u=podBandCoordinate(p);
    float hSeams=min(abs(abs(u-1.36)-0.055),abs(abs(u-1.165)-0.055));
    float vSeam=abs(p.x+0.1025);
    return min(hSeams,vSeam);
}
float podShell(float3 q) {
    // Moth-wing elytron shell: sculpted aerodynamic airfoil with sweeping convex outer curve,
    // separated inner spine margin, sharp angled jet aperture, and protective wingtip cowl.
    float t=clamp((q.y-0.91)/0.67,0.,1.);

    // Inner edge: straight vertical spine margin leaving a clean reveal gap (0.019),
    // rounding gently into the top shoulder dome above y = 1.54
    float xIn=-(0.019+0.01*smoothstep(1.54,1.58,q.y));

    // Outer aerodynamic wing contour:
    // Holds width through shoulder (0.155 at y=1.5), swells to broad belly (0.2325 at y=1.225),
    // and tapers to lower beak (0.173 at y=0.92).
    float dy=q.y-1.225;
    float curve=dy>0.?1.04*dy*dy:0.64*dy*dy;
    float xOut=-0.2325+curve;

    // Top apex shoulder dome:
    float dTop=(q.y-1.58)+3.3*square(q.x+0.085);

    // Bottom cowl rake: cuts from outer wingtip beak (y=0.92) up-inward to inner spine (y=1.05).
    float yCut=q.x<-0.11?1.05+1.25*(q.x+0.11):1.05;
    float dCut=(yCut-q.y)/1.50;

    // Smoothly blend outer contour with top dome and bottom beak to avoid clipped edges:
    float dOuter=xOut-q.x;
    float dTopCorner=smoothIntersection(dOuter,dTop,0.0225);
    float dBeak=smoothIntersection(dOuter,dCut,0.0125);
    float d2D=max(max(q.x-xIn,dCut),max(dTopCorner,dBeak));

    // 3D Airfoil camber with authentic wing volume:
    // Longitudinal crest line runs at x = -0.1025
    float zApex=0.205+0.0675*sin(PI*pow(t,.75));
    float crestX=-0.1025;
    float zRear=zApex-0.9*square(q.x-crestX);
    float zFront=0.1325;
    float dRear=q.z-zRear;
    float dFront=zFront-q.z;
    float dZ=max(dRear,dFront);

    float2 dBox=max(float2(d2D,dZ),0.);
    float shell=length(dBox)+min(max(d2D,dZ),0.)-0.009;

    // Cowl interior cavity: hollows out the inside for the recessed thruster nozzle
    float3 noz=nozzleCoordinates(q);
    float cowlCavity=max(length(noz.xz)-0.049,abs(noz.y+0.0125)-0.0425);
    shell=max(shell,-cowlCavity);

    return shell*.72;
}
// Torso/head, thighs/shins, upper arms/forearms, shoulders, pods, then feet.
static float3x3 partFrame[16];
static float3 partOffset[16];
void rotateFrame(inout float3x3 basis,inout float3 offset,float3 pivot,float angle) {
    float c=cos(angle),s=sin(angle);
    float3x3 rotation=float3x3(float3(1,0,0),float3(0,c,-s),float3(0,s,c));
    basis=mul(basis,rotation);
    offset=mul(offset-pivot,rotation)+pivot;
}
MotionPose poseAt(int pose) {
    MotionPose p={0.,0.,(float2)0,(float2)0,(float2)0,(float2)0,(float2)0,0.,0.,0.,.12};
    if(pose==1) { // Compress over planted, flat soles.
        p.pitch=-.24; p.hip=(float2)(.68); p.knee=(float2)-0.98;
        p.arm=(float2)-0.24; p.elbow=(float2)(.30); p.grip=.40;
    } else if(pose==2) {
        p.pitch=-.10; p.lift=0.2; p.hip=float2(.10,.34); p.knee=float2(-.20,-.64);
        p.arm=float2(.34,.46); p.elbow=(float2)(.38); p.opening=.68; p.thrust=.9;
        p.trail=.22; p.grip=.20;
    } else if(pose==3) {
        p.lift=0.34; p.hip=(float2)(.10); p.knee=(float2)-0.25;
        p.arm=(float2)(.05); p.elbow=(float2)(.20); p.opening=.85; p.thrust=.62;
        p.trail=.12;
    } else if(pose==4) {
        p.pitch=-.60; p.lift=0.42; p.hip=float2(-.06,-.13); p.knee=float2(-.30,-.46);
        p.arm=float2(.95,.90); p.elbow=float2(1.20,1.25);
        p.opening=1.; p.thrust=1.; p.trail=.95; p.grip=.90;
    } else if(pose==5) {
        p.pitch=.18; p.lift=0.32; p.hip=float2(.58,.50); p.knee=float2(-.58,-.50);
        p.arm=float2(.44,.32); p.elbow=(float2)(.30); p.opening=.95; p.thrust=.85;
        p.trail=-.35; p.grip=.08;
    } else if(pose==6) {
        p.pitch=-.32; p.hip=(float2)(.84); p.knee=(float2)-1.12;
        p.arm=(float2)(.55); p.elbow=(float2)(.40); p.opening=.52; p.trail=-.16;
    } else if(pose==7) {
        p.pitch=-.13; p.hip=float2(.75,-.65); p.knee=float2(-.38,-.90);
        p.arm=float2(.75,-1.38); p.elbow=float2(1.20,.15); p.trail=1.; p.grip=.86;
    }
    // Feet articulate independently from shin armor. Contact poses have level soles.
    p.ankle=-p.hip-p.knee-(float2)p.pitch;
    if(pose>=2 && pose<=5) p.ankle=lerp(p.ankle,(float2)(.08),.68);
    if(pose==7) p.ankle=float2(-.12,.35);
    return p;
}
MotionPose blendPose(MotionPose a,MotionPose b,float t) {
    MotionPose p={lerp(a.pitch,b.pitch,t),lerp(a.lift,b.lift,t),
        lerp(a.hip,b.hip,t),lerp(a.knee,b.knee,t),lerp(a.ankle,b.ankle,t),
        lerp(a.arm,b.arm,t),lerp(a.elbow,b.elbow,t),lerp(a.opening,b.opening,t),
        lerp(a.thrust,b.thrust,t),lerp(a.trail,b.trail,t),lerp(a.grip,b.grip,t)};
    return p;
}
void preparePose() {
    motion=poseAt(POSE);
    if(ANIMATE_POSE==1) {
        // Deliberate holds between actions; land before the shells finish closing.
        float t=floorMod(frameGroup.time,12.);
        int a=0,b=1; float u=smoothstep(.75,1.65,t);
        if(t>=1.65) {a=1;b=2;u=smoothstep(1.65,2.55,t);}
        if(t>=2.55) {a=2;b=3;u=smoothstep(2.55,3.6,t);}
        if(t>=4.8) {a=3;b=4;u=smoothstep(4.8,6.,t);}
        if(t>=7.2) {a=4;b=5;u=smoothstep(7.2,8.3,t);}
        if(t>=8.7) {a=5;b=6;u=smoothstep(8.7,10.,t);}
        if(t>=10.25) {a=6;b=0;u=smoothstep(10.25,11.7,t);}
        motion=blendPose(poseAt(a),poseAt(b),u);
    }
    // Cache inverse rigid frames once per pixel, outside all distance queries.
    for(int i=0;i<16;i++) {
        float3x3 basis=float3x3(1,0,0,0,1,0,0,0,1); float3 offset=(float3)0;
        rotateFrame(basis,offset,float3(0,1.075,0),motion.pitch);
        bool otherSide=(i==4 || i==5 || i==8 || i==9 || i==11 || i==15);
        int side=otherSide?1:0;
        if(i==1) {
            rotateFrame(basis,offset,float3(0,1.575,0),-.66*motion.pitch);
            offset.y+=0.0925;
            float3x3 scale=float3x3(float3(1./.88,0,0),float3(0,1./.88,0),float3(0,0,1./.90));
            basis=mul(basis,scale);
            offset=mul(offset-float3(0,1.6,0),scale)+float3(0,1.6,0);
        } else if(i>=2 && (i<12 || i>=14)) {
            if(otherSide) {
                float3x3 mirror=float3x3(float3(-1,0,0),float3(0,1,0),float3(0,0,1));
                basis=mul(basis,mirror); offset.x=-offset.x;
            }
            if(i<=5 || i>=14) {
                rotateFrame(basis,offset,float3(-0.145,1.0275,0),motion.hip[side]);
                if(i==3 || i==5 || i>=14)
                    rotateFrame(basis,offset,float3(-0.19,0.745,0),motion.knee[side]);
                if(i>=14) rotateFrame(basis,offset,float3(-0.2,0.17,-0.0075),motion.ankle[side]);
            } else if(i<=9) {
                rotateFrame(basis,offset,float3(-0.295,1.45,0),motion.arm[side]);
                if(i==7 || i==9) rotateFrame(basis,offset,float3(-0.39,1.175,-0.005),motion.elbow[side]);
            } else rotateFrame(basis,offset,float3(-0.295,1.45,0),.30*motion.arm[side]);
        }
        partFrame[i]=basis; partOffset[i]=offset;
    }
    // Contact is computed from transformed sole support points, then lift is added.
    float lowest=5.;
    for(int foot=14;foot<16;foot++) for(int j=0;j<4;j++) {
        float3 sole=float3(-(0.2+(j<2?-0.12:0.12)),-0.0015,-((j==0 || j==2)?-0.115:0.305));
        float3 world=mul(sole-partOffset[foot],transpose(partFrame[foot]));
        lowest=min(lowest,world.y);
    }
    characterLift=motion.lift-lowest;
    for(int i=0;i<16;i++) partOffset[i]-=mul(float3(0,characterLift,0),partFrame[i]);
}
float3 localPosition(float3 p,int part) { return mul(p,partFrame[part])+partOffset[part]; }
float3 worldPosition(float3 p,int part) {
    if(part==1) return mul(p-partOffset[part],inverse3(partFrame[part]));
    return mul(p-partOffset[part],transpose(partFrame[part]));
}
void preparePack() {
    packOpening=PACK_DEPLOY<0.?motion.opening:clamp(float(PACK_DEPLOY),0.,1.);
    for(int i=0;i<2;i++) {
        float side=i==0?1.:-1.;
        float3x3 mirror=float3x3(float3(side,0,0),float3(0,1,0),float3(0,0,1));
        float3x3 basis=mul(partFrame[0],mirror); float3 offset=mul(partOffset[0],mirror);
        float3 pivot=float3(-0.07,1.52,0.145);
        float settle=ANIMATE_PACK==1?.035*sin(frameGroup.time*1.1+side*.35)*packOpening:0.;
        float opening=clamp(packOpening+settle,0.,1.);
        float2x2 r=rot(-.26*opening);
        float3x3 hinge=float3x3(float3(r[0],0),float3(r[1],0),float3(0,0,1));
        basis=mul(basis,hinge); offset=mul(offset-pivot,hinge)+pivot;
        rotateFrame(basis,offset,pivot,-.085*opening);
        partFrame[12+i]=basis; partOffset[12+i]=offset;
    }
}
float2 podScene(float3 p,bool detail) {
    float bound=box(p-float3(-0.125,1.25,0.19),float3(0.21,0.39,0.15),0.01);
    if(bound>0.075) return float2(bound,-1.);
    float2 h=float2(5.,LILAC);
    // Paint boundaries, shallow reveals and wear use the same curved coordinates.
    float shell=podShell(p),u=podBandCoordinate(p);
    // Pillowed 3D banding relief on all slats:
    float d1=abs(u-1.36)-0.055,d2=abs(u-1.165)-0.055;
    float t1=clamp(1.0-square((u-1.36)/0.055),0.,1.);
    float t2=clamp(1.0-square((u-1.165)/0.055),0.,1.);
    float tMid=clamp(1.0-square((u-1.265)/0.035),0.,1.);
    shell-=0.0019*max(max(t1,t2),tMid);
    if(detail && GEOMETRIC_SEAMS==1 && abs(shell)<0.012)
        shell=carvePanel(shell,podBandEdges(p),0.0012,0.0024,0.0006);
    // Top intake cavity on shoulder dome:
    float topVent=box(p-float3(-0.085,1.515,0.2125),float3(0.012,0.008,0.015),0.0025);
    shell=max(shell,-(topVent+0.003));
    // Badges and vent markings on bands:
    float dBadge=max(abs(p.x+0.1425)-0.011,abs(u-1.165)-0.0045);
    float dPin=length(float2(p.x+0.1775,u-1.165))-0.00225;
    float dPinUpper=length(float2(p.x+0.13,u-1.36))-0.002;
    add(h,shell,LILAC);
    float ivory=min(d1,d2);
    add(h,max(shell-0.00075,ivory),IVORY);
    if(dBadge<0. && shell<0.006) add(h,shell-0.0013,STEEL);
    if(dPin<0. && shell<0.006) add(h,shell-0.00145,JOINT);
    if(dPinUpper<0. && shell<0.006) add(h,shell-0.0014,JOINT);
    if(topVent<0. && p.z>0.19) add(h,topVent,JOINT);
    // Thruster assembly nested inside the cowl:
    float3 noz=nozzleCoordinates(p);
    float flare=0.041-.15*clamp(noz.y,-0.025,0.025);
    float bell=max(abs(noz.y)-0.025,abs(length(noz.xz)-flare)-0.004);
    add(h,bell,JOINT);
    float lip=length(float2(length(noz.xz)-0.0425,noz.y+0.022))-0.0035;
    add(h,lip,STEEL);
    float cyanCore=ell(noz-float3(0,-0.0125,0),float3(0.033,0.006,0.033));
    add(h,cyanCore,CYAN);
    float hub=ell(noz-float3(0,-0.014,0),float3(0.011,0.007,0.011));
    add(h,hub,STEEL);
    // Beveled steel cowl rim around opening:
    float cowlLip=length(float2(length(noz.xz)-0.049,noz.y+0.015))-0.003;
    if(cowlLip<0. && shell<0.006) add(h,shell-0.001,STEEL);
    return h;
}
float3 braidCenter(float t) {
    // The visible root exits the front aperture beside her right cheek.
    float3 root=float3(0.1825,1.7825,-0.1625);
    float aft=max(motion.trail,0.),forward=max(-motion.trail,0.)*2.;
    float3 middle=lerp(float3(0.27,1.49,-0.325),float3(0.46,1.58,-0.1),aft);
    float3 tip=lerp(float3(0.255,1.26,-0.255),float3(0.565,1.55,0.33),aft);
    middle=lerp(middle,float3(0.31,1.575,-0.425),forward);
    tip=lerp(tip,float3(0.3,1.535,-0.525),forward);
    // The first handle clears the cheek and shoulder in every pose.
    float3 a=lerp(root,float3(0.28,1.63,-0.31),t);
    float3 b=lerp(float3(0.28,1.63,-0.31),middle,t);
    float3 c=lerp(middle,tip,t);
    return lerp(lerp(a,b,t),lerp(b,c,t),t);
}
float3 braidStrand(float t,float strand) {
    float3 tangent=normalize(braidCenter(t+.001)-braidCenter(t-.001));
    float3 u=normalize(cross(tangent,float3(0,0,-1))),v=cross(tangent,u);
    // A figure-eight cross-section alternates the over/under crossings of a flat plait.
    float phase=t*6.*PI+strand*2.*PI/3.;
    return braidCenter(t)+(u*cos(phase)*0.028+v*sin(2.*phase)*0.025)*(1.-.30*t);
}
static float4 braidNodes[75];
static float3 braidTip,braidDirection;
static float3 braidBoundsCenter,braidBoundsHalf;
void prepareBraid() {
    float3 lo=(float3)5,hi=(float3)(-5);
    for(int strand=0;strand<3;strand++) for(int i=0;i<25;i++) {
        float t=float(i)/24.;
        float4 node=float4(braidStrand(t,float(strand)),0.025-0.005*t);
        braidNodes[strand*25+i]=node;
        lo=min(lo,node.xyz-(float3)0.0255); hi=max(hi,node.xyz+(float3)0.0255);
    }
    braidBoundsCenter=(lo+hi)*.5; braidBoundsHalf=(hi-lo)*.5;
    braidTip=braidCenter(1.);
    braidDirection=normalize(braidTip-braidCenter(.97));
}
float braidSculpt(float3 p) {
    float bound=box(p-braidBoundsCenter,braidBoundsHalf,0.);
    if(bound>0.0225) return bound;
    float d=5.;
    for(int i=0;i<72;i++) {
        int node=i+i/24;
        d=min(d,cap(p,braidNodes[node].xyz,braidNodes[node+1].xyz,braidNodes[node].w));
    }
    return d;
}

float2 braidScene(float3 p) {
    float2 h=float2(5.,HAIR);
    // Three interwoven strands share one front root throughout the pose sequence.
    add(h,braidSculpt(p),HAIR);
    float3 tip=braidTip;
    float3 tangent=braidDirection;
    float3 tie=p-tip;
    float tieY=dot(tie,tangent);
    float3 radial=tie-tangent*tieY;
    float2 band=float2(length(radial)-0.0365,abs(tieY)-0.0105);
    add(h,min(max(band.x,band.y),0.)+length(max(band,0.))-0.003,GOLD);
    float3 side=normalize(cross(tangent,float3(0,0,-1))),depth=cross(tangent,side);
    float3 tuft=float3(-(dot(tie,side)),tieY-0.0475,-(dot(tie,depth)));
    tuft.x+=0.0065*sin(clamp(tieY/0.105,0.,1.)*PI);
    add(h,ell(tuft,float3(0.024,0.0525,0.017)),HAIR);

    return h;
}
float skullBound(float3 p) {
    return box(p-float3(0,1.9175,0),float3(0.365,0.3125,0.305),0.0025);
}
float2 helmetShell(float3 p) {
    // The same open-bottom shell is used by camera, shadow and occlusion rays.
    float3 q=p-float3(0,1.885,0.0175);
    float outer=ell(q,float3(0.3275,0.3425,0.275));
    float inner=ell(q-float3(0,-0.005,-0.0275),float3(0.2775,0.2935,0.245));
    float width=0.2525*(.84+.16*smoothstep(-0.25,-0.05,q.y));
    float2 opening=(q.xy-float2(0,-0.0225))/float2(width,0.274);
    float cut=max((length(opening)-1.)*width,0.035+q.z);
    float chinHeight=1.67-0.0275*smoothstep(0.1,0.275,abs(p.x));
    chinHeight-=0.015*(1.-smoothstep(-0.075,0.06,-p.z));
    float throatCut=p.y-chinHeight;
    float d=max(max(max(outer,-inner),-cut),-throatCut);
    float hoodMaterial=(cut<0.045 && q.z<-0.0375)?IVORY:LILAC;
    if(throatCut<0.009) hoodMaterial=p.z<-0.05?IVORY:JOINT;
    if(-inner>max(max(outer,-cut),-throatCut)-0.0005) hoodMaterial=JOINT;
    if(q.z>0.12 && q.y<-0.15-0.9*q.x*q.x) hoodMaterial=JOINT;
    return float2(d,hoodMaterial);
}
float2 headScene(float3 p) {
    float3 q,s=p; s.x=-abs(s.x);
    float d;
    float2 h=float2(5.,HAIR);
    float bound=box(p-float3(0.12,1.67,0.14),float3(0.575,0.58,0.625),0.0125);
    if(bound>0.1) return float2(bound,-1.);
    h=braidScene(p);
    float skull=skullBound(p);
    if(skull>0.05) { add(h,skull,-1.); return h; }
    float2 hood=helmetShell(p); add(h,hood.x,hood.y);
    add(h,cylX(s-float3(-0.313,1.85,0.0175),0.109,0.0165)-0.0035,IVORY);
    add(h,cylX(s-float3(-0.336,1.85,0.0175),0.0795,0.0055)-0.0035,LILAC);
    add(h,cylX(s-float3(-0.348,1.85,0.0175),0.05,0.0025)-0.002,GOLD);
    add(h,cylX(s-float3(-0.354,1.85,0.0175),0.0375,0.0025)-0.0015,LILAC);
    // Separate cheek guards extend the hood's shaped ivory edge below each ear.
    q=s-float3(-0.2325,1.735,-0.12);
    q.z-=.16*q.y;
    float guard=fiveSides(q.xy,float2(0.01,0.075),float2(-0.045,0.0525),float2(-0.0425,-0.02),
                         float2(0.0725,-0.0625),float2(0.0875,-0.03));
    add(h,roundedExtrusion(guard,abs(q.z)-0.026,0.009),IVORY);
    add(h,cap(s,float3(-0.23,1.785,-0.065),float3(-0.1625,1.7025,-0.09),0.0135),JOINT);

    // Continuous cheeks, button nose and lip volume, with inset almond eye openings.
    float face=skinGeometry(p);
    q=eyeCoordinates(p);
    float socket=max(eyeOpening(q),-0.0125+q.z);
    face=smoothIntersection(face,-socket,0.003);
    float mouthX=clamp(p.x,-0.066,0.066);
    float smile=smileHeight(mouthX);
    float mouthWidth=max(1.-square(mouthX/0.0665),0.);
    float lipZ=faceFront(float2(mouthX,smile));
    float mouth=max(max(abs(p.y-smile)-(0.001+0.0015*mouthWidth),abs(p.x)-0.0665),p.z-lipZ-0.006);
    float faceWithMouth=max(face,-mouth);
    add(h,faceWithMouth,(-mouth>face)?MOUTH:SKIN);
    if(h.y==SKIN && p.z<-0.2825) {
        float nostril=length((float2(s.x,p.y)-float2(-0.014,1.7955))/float2(0.004,0.00175));
        if(nostril<1.) h.y=MOUTH;
    }

    // Shallow eye surfaces carry the graphic iris and illustrated catchlights.
    q=eyeCoordinates(p);
    add(h,max(ell(q,EYE_RADII),eyeOpening(q)),EYE);
    add(h,lidDistance(q,true),HAIR);
    add(h,lidDistance(q,false),SKIN);
    float3 lashRoot=float3(-0.0485,0.018*eyeOpen,eyeFront(float2(-0.0485,0.018*eyeOpen)));
    float3 lashTip=float3(-0.067,0.0215*eyeOpen,eyeFront(float2(-0.057,0.006))+0.005);
    float3 lashVector=lashTip-lashRoot;
    float lashT=clamp(dot(q-lashRoot,lashVector)/dot(lashVector,lashVector),0.,1.);
    add(h,(length(q-lerp(lashRoot,lashTip,lashT))-lerp(0.006,0.00075,lashT))*.75,HAIR);
    float3 tear=float3(0.051,-0.006*eyeOpen,eyeFront(float2(0.051,-0.006)));
    add(h,ell(q-tear,float3(0.0045,0.0025*eyeOpen+0.0005,0.003)),LIP);
    float browT=clamp((-s.x-0.0345)/0.113,0.,1.);
    float3 brow=bezier(float3(-0.0345,1.9575,-0.258),float3(-0.0825,1.9765,-0.262),float3(-0.1475,1.959,-0.216),browT);
    // Brows follow the forehead surface; a thin depth keeps them from floating in profile.
    brow.z=faceFront(brow.xy)-0.0005;
    add(h,(length((s-brow)*float3(1,1,4.5))-(0.006+0.0045*sin(PI*browT)))*.20,HAIR);

    // The fringe is built from flattened, tapered curve sweeps over the scalp.
    q=p-float3(0,1.9,-0.095);
    d=ell(q,float3(0.2225,0.215,0.175));
    d=smoothIntersection(d,max(1.955-p.y,-p.z-0.1675),0.0125);
    add(h,d,HAIR);
    add(h,hairSculpt(p),HAIR);
    return h;
}

float2 upperLegScene(float3 p) {
    float2 h=float2(5.,JOINT);
    float3 q,s=p;
    float d;
    float bound=box(p-float3(-0.19,0.95,-0.02),float3(0.145,0.23,0.15),0.01);
    if(bound>0.09) return float2(bound,-1.);
    // Symmetric limbs. Armor stays separate to retain joint gaps.

    add(h,cap(s,float3(-0.135,1.025,0),float3(-0.185,0.74,0),0.095),JOINT);
    q=s-float3(-0.1825,0.92,-0.0075); q.xy=mul(q.xy,rot(.12));
    d=smoothIntersection(ell(q,float3(0.1175,0.16,0.114)),q.x-0.011,0.009);
    d=smoothIntersection(d,abs(q.z)-(0.0925-.10*q.y),0.011);
    d=smoothIntersection(d,-q.x-0.09-.06*q.y,0.011);
    add(h,d,LILAC);
    q=s-float3(-0.2225,1.0575,-0.02); q.xy=mul(q.xy,rot(.40));
    float hip=fiveSides(q.xy,float2(0.045,0.09),float2(-0.04,0.09),float2(-0.065,0.0175),
                       float2(-0.025,-0.1),float2(0.06,-0.02));
    d=roundedExtrusion(hip,abs(q.z)-0.095+0.6*q.y*q.y,0.012)*.8;
    add(h,d,(q.z<-0.07)?IVORY:LILAC);
    return h;
}

float2 lowerLegScene(float3 p) {
    float2 h=float2(5.,JOINT);
    float3 q,s=p;
    float d;
    float bound=box(p-float3(-0.205,0.385,-0.0375),float3(0.225,0.45,0.31),0.01);
    if(bound>0.09) return float2(bound,-1.);
    add(h,ell(s-float3(-0.19,0.745,0),float3(0.095,0.085,0.0975)),JOINT);
    add(h,cylX(s-float3(-0.2875,0.745,0),0.046,0.0085)-0.004,JOINT);
    q=s-float3(-0.19,0.745,-0.105); q.xy=mul(q.xy,rot(.12));
    float knee=fiveSides(q.xy,float2(0.06,0.08),float2(-0.0575,0.08),float2(-0.075,-0.01),
                        float2(-0.03,-0.09),float2(0.0525,-0.0675));
    add(h,roundedExtrusion(knee,abs(q.z)-0.0475+1.*q.x*q.x,0.0125)*.9,IVORY);
    // Continuously flared shin shell, with a real arched ankle cutout.
    q=s-float3(-0.2,0,-0.0125);
    q.z+=0.0175*sin(PI*clamp((0.705-q.y)/0.56,0.,1.));
    float side=shinSection(q,0.);
    float ankleCut=ankleOpening(q.xy);
    float kneeSeat=(length(float2(q.x,q.y-0.73)/float2(0.0725,0.075))-1.)*0.0725;
    float ends=max(max(q.y-0.705-.10*q.z,ankleCut),-kneeSeat);
    float outer=chamferIntersection(side,ends,0.009);
    float cavity=shinSection(q,0.019);
    float shin=max(outer,-cavity)*.64;
#if OWNER_COLORWAY == 1
    add(h,shin,-cavity>outer?JOINT:IVORY);
    // The lilac trim follows the arch all the way around the boot.
    add(h,max(shin-0.0015,-ankleCut-0.0475),LILAC);
    // A narrow lilac side panel follows the bowed shin, tapering toward the knee.
    float cheek=fiveSides(q.zy,float2(0.065,0.625),float2(-0.035,0.625),float2(-0.07,0.48),
                          float2(0.0225,0.265),float2(0.13,0.37));
    add(h,max(shin-0.004,max(cheek,0.1+q.x)),LILAC);
#else
    add(h,shin,-cavity>outer?JOINT:LILAC);
    // The ivory trim follows the arch all the way around the boot.
    add(h,max(shin-0.0015,-ankleCut-0.0475),IVORY);
    // A narrow ivory side panel follows the bowed shin, tapering toward the knee.
    float cheek=fiveSides(q.zy,float2(0.065,0.625),float2(-0.035,0.625),float2(-0.07,0.48),
                          float2(0.0225,0.265),float2(0.13,0.37));
    add(h,max(shin-0.004,max(cheek,0.1+q.x)),IVORY);
#endif
    add(h,ell(s-float3(-0.2,0.19,-0.0075),float3(0.085,0.085,0.0925)),JOINT);
    add(h,cap(s,float3(-0.2,0.24,-0.0075),float3(-0.19,0.695,0),0.0715),JOINT);
    add(h,cylX(s-float3(-0.386,0.1825,-0.0025),0.057,0.0125)-0.004,JOINT);
    add(h,cylX(s-float3(-0.402,0.1825,-0.0025),0.039,0.006)-0.003,LILAC);
    return h;
}
float2 footScene(float3 p) {
    float bound=box(p-float3(-0.2,0.15,-0.075),float3(0.185,0.16,0.29),0.015);
    if(bound>0.06) return float2(bound,-1.);
    float2 h=float2(5.,JOINT);
    float3 q,s=p;
    // Sculpted instep and rounded, broad toe, resting on a flat rubber sole.
    add(h,ell(s-float3(-0.2,0.22,-0.0075),float3(0.086,0.0775,0.09)),JOINT);
    q=s-float3(-0.2,0,0);
    float outline=footOutline(q);
    outline=smoothIntersection(outline,(-q.z+.45*q.y-0.365)/1.096,0.0175);
    float crown=1.-.75*clamp(square(q.x/0.165),0.,1.);
    float top=0.1125+0.17*exp(-square((q.z+0.075)/0.18))*crown;
    float shoe=roundedExtrusion(outline,max(0.0275-q.y,q.y-top),0.015)*.45;
    float toe=-q.z-(0.23-0.06*square(q.x/0.165));
    add(h,shoe,(toe>0. || -q.z+.45*q.y>0.345 || q.y<0.075 || q.z>0.085)?LILAC:IVORY);
    add(h,roundedExtrusion(outline+0.0015,abs(q.y-0.024)-0.018,0.0075),JOINT);
    add(h,cylZ(s-float3(-0.2,0.155,0.1725),0.07,0.016)-0.006,JOINT);
    add(h,cylZ(s-float3(-0.2,0.155,0.1915),0.053,0.007)-0.004,LILAC);
    add(h,cylZ(s-float3(-0.2,0.155,0.2025),0.043,0.008),JOINT);
    add(h,ell(s-float3(-0.2,0.155,0.2105),float3(0.035,0.035,0.013)),CYAN);

    return h;
}

float2 upperArmScene(float3 p) {
    float2 h=float2(5.,JOINT);
    float3 q,s=p;
    float d;
    float bound=box(p-float3(-0.34,1.305,0),float3(0.15,0.245,0.115),0.01);
    if(bound>0.09) return float2(bound,-1.);
    // Upper arm, elbow and gently splayed forearm.
    add(h,cap(s,float3(-0.295,1.45,0),float3(-0.385,1.195,0),0.08),JOINT);
    add(h,ell(s-float3(-0.39,1.175,-0.005),float3(0.085,0.08,0.0875)),JOINT);
    return h;
}

float taperedCap(float3 p,float3 a,float3 b,float ra,float rb) {
    float3 axis=b-a;
    float t=clamp(dot(p-a,axis)/dot(axis,axis),0.,1.);
    return (length(p-lerp(a,b,t))-lerp(ra,rb,t))*.88;
}
float handSculpt(float3 p) {
    // A single wrist frame joins the cuff, palm and all five digits.
    float3 q=gauntletCoordinates(p)+float3(0,0.16,0);
    q.xz=mul(q.xz,rot(-.28));
    float bound=box(q-float3(0.0125,-0.08,0.0125),float3(0.105,0.1225,0.075),0.005);
    if(bound>0.03) return bound;
    float wrist=cap(q*float3(1,1,1.15),float3(0,0.0225,0),float3(0,-0.055,0),0.04)/1.15;
    float3 palmP=abs(q-float3(0,-0.07,0.002))/float3(0.067,0.054,0.04);
    float palm=(pow(dot(pow(palmP,(float3)2.8),(float3)1),1./2.8)-1.)*0.04;
    float d=smoothUnion(wrist,palm,0.013);
    for(int i=0;i<4;i++) {
        float x=0.0465-0.031*float(i);
        float extra=i==1?0.006:(i==2?0.0035:(i==3?-0.0105:0.));
        float3 a=float3(x,-0.1055,-0.003);
        float3 b=lerp(float3(x,-0.145-extra,-0.004),float3(x,-0.1285,-0.019),motion.grip);
        float3 c=lerp(float3(x*.96,-0.1675-extra,0.0165),float3(x*.98,-0.146,0.017),motion.grip);
        float3 e=lerp(float3(x*.92,-0.161-extra,0.0385),float3(x*.94,-0.1175,0.0435),motion.grip);
        float finger=taperedCap(q,a,b,0.015,0.014);
        finger=smoothUnion(finger,taperedCap(q,b,c,0.014,0.0125),0.0035);
        finger=smoothUnion(finger,taperedCap(q,c,e,0.0125,0.0115),0.003);
        d=smoothUnion(d,finger,0.005);
    }
    // The thumb originates on the medial palm and crosses the curled fingers.
    float3 a=float3(0.045,-0.054,0.005),b=float3(0.0745,-0.086,0.009);
    float3 c=lerp(float3(0.0725,-0.1215,0.0265),float3(0.051,-0.1155,0.0465),motion.grip);
    float3 e=lerp(float3(0.0485,-0.134,0.042),float3(0.013,-0.1205,0.054),motion.grip);
    float thumb=taperedCap(q,a,b,0.0225,0.019);
    thumb=smoothUnion(thumb,taperedCap(q,b,c,0.019,0.016),0.006);
    thumb=smoothUnion(thumb,taperedCap(q,c,e,0.016,0.013),0.004);
    return smoothUnion(d,thumb,0.009);
}

float2 forearmScene(float3 p) {
    float2 h=float2(5.,JOINT);
    float3 q,s=p;
    float d;
    float bound=box(p-float3(-0.48,1.01,-0.015),float3(0.22,0.345,0.2),0.01);
    if(bound>0.09) return float2(bound,-1.);
    q=gauntletCoordinates(p);
    float2 barrelRadii=float2(0.0885,0.0925)+0.0275*smoothstep(-0.16,0.11,q.y);
    float barrelEnds=max(q.y-0.155-.28*q.x,-q.y-0.16-.12*q.x);
    d=roundedExtrusion(curvedSection(q.xz+float2(-0.0125,0),barrelRadii),barrelEnds,0.0125)*.8;
    add(h,d,LILAC);
    add(h,gauntletShell(q),IVORY);
    float3 lens=q-float3(-0.12,-0.1175,-0.09); lens.xz=mul(lens.xz,rot(-.86));
    add(h,cylZ(lens,0.0595,0.0125)-0.006,IVORY);
    add(h,cylZ(lens-float3(0,0,-0.0155),0.0455,0.008)-0.003,JOINT);
    add(h,ell(lens-float3(0,0,-0.026),float3(0.03,0.03,0.0115)),CYAN);
    add(h,handSculpt(p),JOINT);

    return h;
}

float2 shoulderScene(float3 p) {
    float2 h=float2(5.,JOINT);
    float3 q,s=p;
    float d;
    float bound=box(p-float3(-0.385,1.465,-0.0075),float3(0.21,0.185,0.18),0.01);
    if(bound>0.09) return float2(bound,-1.);
    // Swept overlapping plates. The upper saddle sits behind the long outer blade.
    q=s-float3(-0.305,1.5775,0.0125);
    q.y*=1.25;
    float saddle=ell(q-float3(-0.02,-0.025,0),float3(0.15,0.105,0.145));
    float saddleInside=ell(q-float3(-0.02,-0.0425,0),float3(0.1225,0.0825,0.1175));
    saddle=smoothIntersection(saddle,max(-saddleInside,-0.0175-q.y+.32*q.x),0.006);
    add(h,saddle*.85,LILAC);
    q=s-float3(-0.31,1.5,-0.0075);
    d=shoulderPlate(q);
    add(h,d,LILAC);
    float stripe=(q.y-.43*q.x+0.0125)/1.09;
    add(h,max(d-0.001,abs(stripe)-0.0185),IVORY);
    add(h,cylZ(s-float3(-0.3,1.49,-0.161),0.073,0.0185)-0.006,LILAC);
    add(h,cylZ(s-float3(-0.3,1.49,-0.1865),0.0295,0.005)-0.0035,IVORY);

    return h;
}

float2 torsoScene(float3 p) {
    float2 h=float2(5.,JOINT);
    float3 q,s=p;
    float d;
    float bound=box(p-float3(0,1.265,0.08),float3(0.365,0.42,0.395),0.01);
    if(bound>0.09) return float2(bound,-1.);
    // A compact breastplate with a curved ivory inset and a beveled lower edge.
    add(h,ell(p-float3(0,1.29,0),float3(0.1625,0.285,0.1225)),JOINT);
    float chest=ell(p-float3(0,1.395,-0.0125),float3(0.24,0.1725,0.16));
    chest=chamferIntersection(chest,(1.23+.24*abs(p.x)-p.y)/1.03,0.008);
    float breastPlane=(-p.z+.32*abs(p.x)+.20*(1.4-p.y)-0.1685)/1.069;
    chest=smoothIntersection(chest,breastPlane,0.0125);
    float neckHole=ell(p-float3(0,1.5675,-0.0275),float3(0.1175,0.08,0.15));
    chest=smoothIntersection(chest,-neckHole,0.009);
    // A broad ivory breastplate sits beneath the lilac upper yoke.
    float yoke=max(p.y-(1.4+.24*abs(p.x)),1.265+.23*abs(p.x)-p.y);
    yoke=max(yoke,abs(p.x)-0.1975);
    add(h,chest,(yoke<0. && p.z<-0.065)?IVORY:LILAC);
    // Two overlapping abdominal plates connect the breastplate to the belt.
    for(int i=0;i<2;i++) {
        q=p-float3(0,1.215-0.0675*float(i),-0.122);
        float abdomen=fiveSides(q.xy,float2(0.125,0.0425),float2(-0.125,0.0425),
                                float2(-0.1325,-0.004),float2(0,-0.055),float2(0.1325,-0.004));
        float plate=roundedExtrusion(abdomen,abs(q.z)+.18*abs(q.x)-0.0275,0.007);
        add(h,plate,LILAC);
    }
    q=p-float3(0,1.09,0);
    add(h,chamferIntersection(ell(q,float3(0.1925,0.065,0.1325)),abs(q.y)-0.03,0.006),OCHRE);
    q=p-float3(0,1.105,-0.1375);
    float buckle=fiveSides(q.xy,float2(0.055,0.0425),float2(-0.055,0.0425),
                           float2(-0.075,-0.01),float2(0,-0.045),float2(0.075,-0.01));
    add(h,roundedExtrusion(buckle,abs(q.z)-0.0225,0.0075),OCHRE);
    add(h,ell(p-float3(0,1.02,0),float3(0.195,0.1425,0.1475)),JOINT);
    q=p-float3(0,1.015,-0.1325);
    float pelvis=fiveSides(q.xy,float2(0.1175,0.0775),float2(-0.1175,0.0775),
                          float2(-0.135,-0.0075),float2(0,-0.11),float2(0.135,-0.0075));
    float pelvicPlate=roundedExtrusion(pelvis,abs(q.z)+.22*abs(q.x)-0.036,0.009);
    add(h,pelvicPlate,LILAC);

    // Central spine and compact mounts sit between the two curved flight shells.
    add(h,box(p-float3(0,1.305,0.1875),float3(0.014,0.245,0.012),0.004),JOINT);
    for(int i=0;i<5;i++)
        add(h,box(p-float3(0,1.15+0.0675*float(i),0.196),float3(0.0125,0.011,0.007),0.0025),JOINT);
    s=p; s.x=-abs(s.x);
    add(h,cap(s,float3(-0.01,1.53,0.15),float3(-0.085,1.53,0.16),0.009),JOINT);
    add(h,cylZ(s-float3(-0.085,1.53,0.16),0.013,0.007)-0.002,STEEL);
    q=p-float3(0,1.06,0.1075);
    add(h,smoothIntersection(ell(q,float3(0.0825,0.095,0.0375)),-q.y-0.085+.6*abs(q.x),0.01),OCHRE);
    // The collar is seated in the breastplate. A broad, low undersuit yoke
    // joins it to the shoulders; there is no exposed spherical neck joint.
    float neckYoke=ell(p-float3(0,1.4935,-0.0025),float3(0.175,0.07,0.115));
    add(h,neckYoke,JOINT);
    q=p-float3(0,1.5385,-0.045);
    float taper=1.-.09*clamp(q.y/0.022,-1.,1.);
    float collarSide=(length(q.xz/(float2(0.0905,0.071)*taper))-1.)*0.0645;
    float collarHole=(length(q.xz/float2(0.065,0.05))-1.)*0.05;
    // The front edge dips slightly to follow the throat, rather than a level choker.
    float collarHeight=q.y+0.006*smoothstep(-0.025,0.06,-q.z);
    float collar=roundedExtrusion(collarSide,abs(collarHeight)-0.022,0.005)*.80;
    add(h,max(collar,-collarHole),OCHRE);
    add(h,cylZ(p-float3(-0.02,1.533,-0.1175),0.011,0.002)-0.001,JOINT);

    return h;
}

float2 torsoPackMountScene(float3 p) {
    float2 h=float2(5.,JOINT);
    // Central spine column terminating neatly between the wing shoulder domes
    add(h,box(p-float3(0,1.295,0.1875),float3(0.014,0.24,0.012),0.004),JOINT);
    // 5 prominent ribbed vertebrae plates protruding proudly between the wings
    for(int i=0;i<5;i++)
        add(h,box(p-float3(0,1.13+0.075*float(i),0.196),float3(0.0125,0.011,0.007),0.0025),JOINT);
    // Base bracket between thruster cutouts
    add(h,box(p-float3(0,1.05,0.1825),float3(0.019,0.02,0.012),0.004),JOINT);
    // Upper mounting arms and cylindrical hinge pivots (seated behind the wings)
    float3 s=p; s.x=-abs(s.x);
    add(h,cap(s,float3(-0.01,1.51,0.14),float3(-0.07,1.51,0.145),0.008),JOINT);
    add(h,cylZ(s-float3(-0.07,1.51,0.145),0.011,0.006)-0.002,STEEL);
    // Body-facing mounting backplate (inferred mounting surfaces)
    add(h,box(s-float3(-0.065,1.31,0.145),float3(0.04,0.16,0.01),0.0075),JOINT);
    return h;
}

float2 scene(float3 p) {
    float2 h=float2(p.y,0.);
#if ISOLATE_PACK == 1
    float bound=box(p-float3(0,1.275+characterLift,0.2),float3(0.325,0.375,0.225),0.01);
    if(bound>0.075) { add(h,bound,-1.); return h; }
    float2 part=torsoPackMountScene(localPosition(p,0)); add(h,part.x,part.y);
    for(int i=0;i<2;i++) {
        part=podScene(localPosition(p,12+i),true);
        add(h,part.x,part.y+20.*float(12+i));
    }
    return h;
#else
    float bound=box(p-float3(0,1.075+characterLift,0),float3(0.95,1.35,1.5),0.015);
    if(bound>0.175) { add(h,bound,-1.); return h; }
    float2 part=torsoScene(localPosition(p,0)); add(h,part.x,part.y);
    part=headScene(localPosition(p,1)); add(h,part.x*.88,part.y+20.);
    for(int side=0;side<2;side++) {
        int leg=2+2*side,arm=6+2*side,shoulder=10+side;
        part=upperLegScene(localPosition(p,leg)); add(h,part.x,part.y+20.*float(leg));
        part=lowerLegScene(localPosition(p,leg+1)); add(h,part.x,part.y+20.*float(leg+1));
        part=footScene(localPosition(p,14+side)); add(h,part.x,part.y+20.*float(14+side));
        part=upperArmScene(localPosition(p,arm)); add(h,part.x,part.y+20.*float(arm));
        part=forearmScene(localPosition(p,arm+1)); add(h,part.x,part.y+20.*float(arm+1));
        part=shoulderScene(localPosition(p,shoulder)); add(h,part.x,part.y+20.*float(shoulder));
    }
    for(int i=0;i<2;i++) {
        part=podScene(localPosition(p,12+i),true);
        add(h,part.x,part.y+20.*float(12+i));
    }
    return h;
#endif
}
float4 surfaceGradient(float3 p) {
    float3 n=(float3)0;
    for(int i=0;i<4;i++) {
        float3 e=float3(-.5773503,.5773503,-.5773503)*(2.*float3(float((i+3)/2%2),float(i/2%2),float(i%2))-1.);
        n+=e*scene(p+e*0.00075).x;
    }
    // Preserve the gradient magnitude: conservative distance estimates are not unit SDFs.
    float magnitude=length(n)/0.001;
    return float4(normalize(n),clamp(magnitude,.12,1.5));
}
float2 secondaryScene(float3 p) {
    // Secondary rays resolve the larger forms; eyelids and grooves use local shading.
    float2 h=float2(p.y,0.),piece;
#if ISOLATE_PACK == 1
    piece=torsoPackMountScene(localPosition(p,0)); add(h,piece.x,piece.y);
    for(int i=0;i<2;i++) {
        piece=podScene(localPosition(p,12+i),false); add(h,piece.x,piece.y);
    }
    return h;
#else
    float3 q=localPosition(p,1);
    float skull=skullBound(q);
    if(skull>0.05) add(h,skull*.88,-1.);
    else {
        float2 hood=helmetShell(q); add(h,hood.x*.88,hood.y);
        add(h,skinGeometry(q)*.88,SKIN);
    }
    float braidBound=box(q-braidBoundsCenter,braidBoundsHalf+(float3)0.05,0.);
    if(braidBound>0.05) add(h,braidBound*.88,-1.);
    else {
        float braid=min(cap(q,braidCenter(0.),braidCenter(.40),0.0475),
                        cap(q,braidCenter(.40),braidTip,0.039));
        add(h,braid*.88,HAIR);
    }
    piece=torsoScene(localPosition(p,0)); add(h,piece.x,piece.y);
    for(int side=0;side<2;side++) {
        int leg=2+side*2,arm=6+side*2,shoulder=10+side;
        piece=upperLegScene(localPosition(p,leg)); add(h,piece.x,piece.y);
        piece=lowerLegScene(localPosition(p,leg+1)); add(h,piece.x,piece.y);
        piece=footScene(localPosition(p,14+side)); add(h,piece.x,piece.y);
        piece=upperArmScene(localPosition(p,arm)); add(h,piece.x,piece.y);
        piece=forearmScene(localPosition(p,arm+1)); add(h,piece.x,piece.y);
        piece=shoulderScene(localPosition(p,shoulder)); add(h,piece.x,piece.y);
    }
    for(int i=0;i<2;i++) {
        piece=podScene(localPosition(p,12+i),false); add(h,piece.x,piece.y);
    }
    return h;
#endif
}
float shadow(float3 p,float3 l,float distanceScale) {
    float v=1.,t=0.0125;
    for(int i=0;i<SHADOW_STEPS;i++) {
        float2 hit=secondaryScene(p+l*t);
        float d=hit.x;
        // An actual blocker is fully occluded. Returning the current penumbra
        // estimate here leaks quantized light into the nose and throat shadows.
        if(d<0.0004) return 0.;
        // Bounds accelerate traversal but do not represent shadow-casting surfaces.
        if(floorMod(hit.y,20.)<18.) v=min(v,7.*d/(t*distanceScale));
        // Small near-surface steps keep grazing rays from skipping narrow plate details.
        t+=clamp(d*.9,0.002,0.05);
        if(t>3.) break;
    }
    return clamp(v,0.,1.);
}
float3 materialAlbedo(float m);
float ambientOcclusion(float3 p,float3 n,float distanceScale,out float3 bleed) {
    float v=0.,w=1.;
    bleed=(float3)0;
    for(int i=1;i<=4;i++) {
        float t=0.0275*float(i);
        float2 hit=secondaryScene(p+n*t);
        if(floorMod(hit.y,20.)<18.) {
            float separation=hit.x/distanceScale;
            v+=max(t-separation,0.)*w;
            // Reuse these probes for restrained neighbor-color bounce, not full GI.
            float proximity=clamp(1.-separation/t,0.,1.);
            bleed+=materialAlbedo(floorMod(hit.y,20.))*proximity*w;
        }
        w*=.55;
    }
    bleed*=.16;
    return clamp(1.-5.4*v,.25,1.);
}
struct Surface {
    float3 base;
    float roughness;
    float metal;
    float coat;
    float specular;
};
Surface makeSurface(float3 base,float roughness,float metal,float coat,float specular) {
    Surface s={base,roughness,metal,coat,specular};
    return s;
}
Surface material(float m) {
    // Linear-light colors; colored paint is a dielectric, not bare metal.
    if(m>12.5) return makeSurface(float3(.065,.014,.004),.6,0.,0.,.02);
    if(m>11.5) return makeSurface(float3(.28,.112,.062),.57,0.,0.,.018);
    if(m>10.5) return makeSurface(float3(.36,.20,.035),.65,0.,0.,.025);
    if(m>8.5) return makeSurface(float3(.20,.23,.26),.32,.78,0.,.04);
    if(m<.5) return makeSurface(float3(.38,.35,.32),.90,0.,0.,.025);
    if(m<1.5) return makeSurface(float3(.30,.17,.45),.36,0.,.30,.04);
    if(m<2.5) return makeSurface(float3(.86,.67,.45),.40,0.,.24,.04);
    if(m<3.5) return makeSurface(float3(.025,.028,.035),.65,0.,0.,.03);
    if(m<4.5) return makeSurface(float3(.33,.134,.047),.59,0.,0.,.022);
    if(m<5.5) return makeSurface(float3(.012,.009,.007),.52,0.,0.,.025);
    if(m<6.5) return makeSurface(float3(.64,.365,.095),.34,.72,.08,.04);
    if(m<7.5) return makeSurface(float3(.012,.22,.29),.16,.18,.5,.04);
    return makeSurface(float3(.84,.79,.69),.27,0.,0.,.006);
}
float3 materialAlbedo(float m) { return material(m).base; }
float3 skinTransmission(float3 p,float3 normal,float3 light) {
    // March only the skin volume from just inside the surface to its light-facing
    // exit. A bounded thin-feature transmission estimate, not a scattering solver.
    float3 origin=p-normal*0.003;
    float travel=0.003;
    for(int i=0;i<10;i++) {
        float d=skinGeometry(origin+light*travel);
        if(d>0.0002) return exp(-travel*float3(28.,64.,110.));
        travel+=clamp(-d*.85,0.003,0.0225);
        if(travel>0.14) break;
    }
    return (float3)0; // Opaque when this short march cannot find an exit.
}
void eyeSurface(float3 p,float pixel,inout Surface surf,out float occlusion,out float3 glints) {
    // Authored graphic iris under the shallow eye surface. No refracted fibers.
    float3 q=eyeCoordinates(p);
    float2 uv=float2(q.x*float(sign(p.x)),q.y)*float2(2.,1.72)-float2(.002,.003);
    float aa=max(pixel*2.2,.00065),radius=length(uv);
    float irisMask=1.-smoothstep(.067-aa,.067+aa,radius);
    float upperShade=smoothstep(-.045,.043,uv.y);
    float3 amber=lerp(float3(.22,.085,.022),float3(.040,.014,.006),upperShade);
    // A curved honey-colored lower band, cut away by the offset dark iris center.
    float crescent=(1.-smoothstep(.057-aa,.057+aa,radius))
                  *smoothstep(.047-aa,.047+aa,length(uv-float2(0,.018)));
    amber=lerp(amber,float3(.48,.235,.055),crescent*.68);
    amber*=1.-.76*smoothstep(.058,.067,radius);
    float pupilRadius=length(uv/float2(.032,.046));
    float pupil=1.-smoothstep(1.-aa/.030,1.+aa/.030,pupilRadius);
    amber=lerp(amber,float3(.006,.003,.002),pupil);
    surf.base=lerp(float3(.84,.79,.69),amber,irisMask);
    float upper=eyelidHeights(q.x).x;
    occlusion=.38+.62*smoothstep(0.001,0.039,upper-q.y);
    surf.base*=occlusion;
    // Both eyes share the same illustrated light direction; highlights are emission
    // so the graphic design survives the studio lighting and the iris stays dark.
    float primary=length((uv-float2(-.021,.025))/float2(.011,.015));
    float spot=1.-smoothstep(1.-aa/.011,1.+aa/.011,primary);
    float dotGlint=1.-smoothstep(.006-aa,.006+aa,length(uv-float2(.029,-.035)));
    glints=float3(2.8,2.65,2.4)*(spot+.60*dotGlint)*irisMask;
}

// Analytic panel lines and fastener recesses, evaluated only at a surface hit.
// These are shallow shading details, so the ray-marched silhouette stays intact.
float2 armorDetail(float3 p,float m) {
    float3 q=p; q.x=-abs(q.x);
    float seam=0.5,rivet=0.5;
    if(p.y>1.64) {
        q=p-float3(0,1.885,0.0175);
        if(m==IVORY) {
            float a=atan2(-q.x,q.y);
            seam=abs(sin(4.*a+.2))*0.07;
            // A small pair of cheek fasteners, set into the ivory rim.
            rivet=length(float2(abs(q.x)-0.215,q.y+0.195));
        } else {
            float a=atan2(-q.x,-q.z);
            seam=min(abs(abs(a)-.67),abs(abs(a)-1.9))*0.24;
            seam=min(seam,abs(q.y-0.195));
            rivet=length(float2(abs(q.x)-0.2,q.y-0.18));
        }
        // Side discs have their own concentric machining line.
        if(abs(p.x)>0.3395 && length(q.yz)<0.1075) {
            seam=abs(length(q.yz)-0.0695); rivet=0.5;
        }
        return float2(seam,rivet);
    }
    if(q.x<-0.36 && p.y<1.28 && p.y>0.85) {
        q=gauntletCoordinates(p);
        if(m==IVORY) {
            seam=min(abs(q.y-0.065+.28*q.z),abs(q.y+0.08-.30*q.z));
            seam=min(seam,abs(-q.z-0.05+.25*q.y));
            rivet=length(float2(q.y-0.1275,q.z-0.0225));
        } else {
            seam=min(abs(q.y-0.1325),abs(q.y+0.125));
            seam=min(seam,abs(q.x-0.09));
            rivet=length(float2(q.x-0.055,q.y-0.1));
        }
        return float2(seam,rivet);
    } else if(p.y<0.705 && p.y>0.2) {
        q-=float3(-0.2,0,-0.0125);
        q.z+=0.0175*sin(PI*clamp((0.705-q.y)/0.56,0.,1.));
        float2 radii=shinRadii(q.y);
        float angle=atan2(-q.x/radii.x,-q.z/radii.y);
        seam=min(abs(abs(angle)-.66),abs(abs(angle)-2.40))*0.115;
        seam=min(seam,abs(q.y-0.6375));
        seam=min(seam,abs(ankleOpening(q.xy)+0.049));
        rivet=length(float2(abs(q.x)-0.07,q.y-0.55));
        return float2(seam,rivet);
    } else if(p.y<0.2) {
        q-=float3(-0.2,0,0);
        float toe=-q.z-(0.23-0.06*square(q.x/0.165));
        seam=min(abs(toe)*.65,abs(footOutline(q)+0.016));
        rivet=length(float2(abs(q.x)-0.1,q.z+0.19));
        return float2(seam,rivet);
    } else if(p.y>1.27 && q.x<-0.235 && p.z<0.16) {
        if(p.z<-0.1775 && length(p.xy-float2(float(sign(p.x))*0.3,1.49))<0.085)
            return float2(abs(length(p.xy-float2(float(sign(p.x))*0.3,1.49))-0.0675),0.5);
        q-=float3(-0.31,1.5,-0.0075);
        seam=abs(shoulderProfile(q.xy)+0.016);
        rivet=length(q.xy-float2(-0.1575,-0.1));
        return float2(seam,rivet);
    } else if(p.y>1.28 && q.x>-0.215) {
        // A shallow contour follows the upper lip of the ivory chest inset.
        seam=abs(p.y-(1.4825-0.041*exp(-square(p.x/0.09))));
        if(p.y<1.455) seam=min(seam,abs(p.x));
        rivet=length(float2(abs(p.x)-0.1375,p.y-1.4575));
        return float2(seam,rivet);
    } else return (float2)0.5;
}
float armorHeight(float2 d) {
    float groove=exp(-square(d.x/0.00135));
    float recess=exp(-square(square(d.y/0.004)));
    return -0.00075*groove-0.001*recess;
}
float2 detailAt(float3 p,float m,int part) {
    float3 q=localPosition(p,part);
    if(part==12 || part==13) {
        float u=podBandCoordinate(q);
        float notch1=length(float2(max(abs(q.x+0.1825)-0.0075,0.),u-1.44));
        float notch2=length(float2(max(abs(q.x+0.1775)-0.0075,0.),u-1.19));
        return float2(podBandEdges(q),min(notch1,notch2));
    }
    return armorDetail(q,m);
}
float hash31(float3 p) {
    p=frac(p*.1031); p+=dot(p,p.yzx+33.33);
    return frac((p.x+p.y)*p.z);
}
float noise3(float3 p) {
    float3 a=floor(p),f=frac(p); f=f*f*(3.-2.*f);
    return lerp(lerp(lerp(hash31(a),hash31(a+float3(1,0,0)),f.x),
                   lerp(hash31(a+float3(0,1,0)),hash31(a+float3(1,1,0)),f.x),f.y),
               lerp(lerp(hash31(a+float3(0,0,1)),hash31(a+float3(1,0,1)),f.x),
                   lerp(hash31(a+float3(0,1,1)),hash31(a+float3(1,1,1)),f.x),f.y),f.z);
}
float scratches(float2 uv,float pixel,float seed) {
    float2 cell=floor(uv*7.),q=frac(uv*7.);
    float h=hash31(float3(cell,seed));
    float2 center=.25+.5*float2(hash31(float3(cell,seed+2.)),hash31(float3(cell,seed+7.)));
    q=mul(q-center,rot(-.65+1.2*h))/7.;
    float halfLength=.009+.032*hash31(float3(cell,seed+13.));
    float d=length(float2(max(abs(q.x)-halfLength,0.),q.y));
    float width=.0006+.0008*h,aa=max(pixel,.0005);
    return (1.-smoothstep(width,width+aa,d))*smoothstep(.62,.77,h)*min(1.,width/aa);
}
void armorWear(float3 p,float3 n,float2 detail,float pixel,int part,inout Surface surf) {
    if(WEAR==0) return;
    // Authored contact zones keep wear off broad, protected paint surfaces.
    float edge=detail.x+0.01,contact=.05,dust=0.;
    float3 q=p; q.x=-abs(q.x);
    if(part==3 || part==5 || part==14 || part==15) {
        q-=float3(-0.2,0,0);
        if(q.y<0.2) {
            edge=min(abs(q.y-0.0675),abs(-q.z+.45*q.y-0.345)*.8);
            contact=.40+.6*smoothstep(0.165,0.325,-q.z);
            dust=(1.-smoothstep(0.035,0.12,q.y))*.09;
        } else if(q.y<0.71) {
            float3 shin=q;
            shin.z+=0.0125+0.0175*sin(PI*clamp((0.705-q.y)/0.56,0.,1.));
            float side=shinSection(shin,0.);
            edge=max(min(abs(ankleOpening(q.xy)),abs(q.y-0.705-.10*q.z)),abs(side));
            contact=.24;
        } else { edge=min(edge,.7*abs(q.y-0.79)); contact=.45; }
    } else if(part==7 || part==9) {
        q=gauntletCoordinates(p);
        float side=curvedSection(q.xz,float2(0.0885,0.0925)+0.0275*smoothstep(-0.16,0.11,q.y));
        float end=min(abs(q.y+0.16+.12*q.x),abs(q.y-0.155-.28*q.x));
        edge=min(detail.x+0.005,max(end,abs(side)));
        contact=.34;
    } else if(part==12 || part==13) {
        edge=min(podBandEdges(q)+0.006,min(abs(q.y-0.96),abs(q.y-1.58)));
        contact=.15;
    } else if(part>=10) {
        q-=float3(-0.31,1.5,-0.0075);
        float radius=0.155*(1.-.5*smoothstep(0.025,0.225,-q.y));
        float side=curvedSection(float2(q.x-0.01,q.z),float2(0.25,radius));
        edge=min(edge,max(abs(shoulderProfile(q.xy)),abs(side)));
        contact=.14;
    } else if(part==1) {
        q=p-float3(0,1.885,0.0175);
        float opening=(length((q.xy-float2(0,-0.0225))/float2(0.2525,0.274))-1.)*0.2525;
        if(q.z<-0.0375) edge=min(edge,abs(opening-0.045));
        contact=.035;
    }
    // Three-dimensional masks stay attached through all joint and camera motion.
    float3 sampleP=p+float3(-(float(part)*0.855),0,-(0.185*float(part)));
    float coarse=noise3(sampleP*float3(-64.,64.,-64.)),fine=noise3(sampleP*float3(-270.,270.,-270.));
    float aa=max(pixel,0.00035);
    float reach=0.001+0.007*smoothstep(.54,.76,coarse);
    float chips=(1.-smoothstep(reach-aa,reach+aa,edge))*smoothstep(.50,.72,fine);
    chips*=1.-smoothstep(0.0025,0.008,pixel);
    float3 weights=pow(abs(n),(float3)6.); weights/=max(dot(weights,(float3)1),.001);
    float scratch=dot(weights,float3(scratches(p.zy*float2(-2.,2.),pixel*2.,float(part)+1.),
                                   scratches(p.xz*-2.,pixel*2.,float(part)+8.),
                                   scratches(p.xy*float2(-2.,2.),pixel*2.,float(part)+19.)));
    scratch*=.16+.65*contact+.45*exp(-edge/0.02);
    float3 primer=lerp(float3(.12,.105,.15),float3(.23,.21,.19),surf.base.r);
    surf.base=lerp(surf.base,primer,chips*.85);
    float metal=chips*smoothstep(.65,.83,fine);
    surf.base=lerp(surf.base,float3(.32,.33,.35),metal);
    surf.base=lerp(surf.base,surf.base*.60+float3(.16,.145,.12),scratch*.8);
    surf.base=lerp(surf.base,float3(.25,.205,.16),dust*(.55+.45*coarse));
    surf.roughness=clamp(surf.roughness+.035*(noise3(sampleP*float3(-18.,18.,-18.))-.5)
                        +.12*scratch+.10*chips+.06*contact*coarse,.20,.75);
    surf.metal=max(surf.metal,metal*.7);
    surf.coat*=1.-clamp(chips+scratch*.5+dust,0.,1.);
}
void finishArmor(float3 p,float3 geometricNormal,float m,float pixel,int part,
                 inout float3 n,inout Surface surf) {
    float2 d=detailAt(p,m,part);
    float aa=max(pixel*.65,0.000325);
    float panelLine=1.-smoothstep(0.00075,0.00075+aa,d.x);
    float lip=1.-smoothstep(0.00075,0.00075+aa,abs(d.x-0.0025));
    float recess=1.-smoothstep(0.0025,0.004+aa,d.y);
    surf.base*=1.-.38*panelLine-.17*recess;
    surf.base=lerp(surf.base,surf.base*1.12,lip*.6);
    surf.roughness+=panelLine*.09+recess*.13;
    // Finite differences of the shallow height field give a recessed normal.
    float e=max(pixel*.6,0.0004),a=armorHeight(d);
    float3 g=float3(armorHeight(detailAt(p+float3(-e,0,0),m,part))-a,
                armorHeight(detailAt(p+float3(0,e,0),m,part))-a,
                armorHeight(detailAt(p+float3(0,0,-e),m,part))-a)*float3(-1,1,-1)/e;
    g-=geometricNormal*dot(g,geometricNormal);
    g*=min(1.,.3/max(length(g),.0001));
    n=normalize(geometricNormal-g*.65);
    armorWear(localPosition(p,part),normalize(mul(geometricNormal,partFrame[part])),d,pixel,part,surf);
}

float3 fresnel(float vh,float3 f0) {
    return f0+(1.-f0)*pow(clamp(1.-vh,0.,1.),5.);
}
float distribution(float nh,float roughness) {
    float a=roughness*roughness,a2=a*a;
    float d=nh*nh*(a2-1.)+1.;
    return a2/max(PI*d*d,.00001);
}
float3 directLight(Surface s,float3 n,float3 v,float3 l,float3 radiance,float visibility,float skin) {
    float nl=max(dot(n,l),0.),nv=max(dot(n,v),.001);
    float3 h=normalize(v+l);
    float nh=max(dot(n,h),0.),vh=max(dot(v,h),0.);
    float3 f0=lerp((float3)s.specular,s.base,s.metal),f=fresnel(vh,f0);
    // A finite light size broadens highlights rather than producing pinpricks.
    float rough=sqrt(s.roughness*s.roughness+.018);
    float k=square(rough+1.)/8.;
    float gv=nv/(nv*(1.-k)+k),gl=nl/(nl*(1.-k)+k);
    float3 spec=distribution(nh,rough)*gv*gl*f/max(4.*nl*nv,.001);
    float diffuse=lerp(nl,max((dot(n,l)+.30)/1.30,0.),skin);
    float cel=lerp(.16,.95,smoothstep(-.035,.10,dot(n,l)));
    diffuse=lerp(diffuse,cel,float(CEL_STYLE));
    float3 result=(1.-f)*(1.-s.metal)*s.base*(diffuse/PI);
    float terminator=exp(-square(dot(n,l)/.10));
    result+=float(CEL_STYLE)*s.base*float3(.11,.022,.005)*terminator;
    result+=spec*nl;
    float coat=distribution(nh,.25)*gv*gl/max(4.*nv,.001);
    result+=s.coat*.04*coat;
    return result*radiance*visibility;
}
float softbox(float3 r,float3 direction,float2 size,float blur) {
    float3 right=normalize(cross(direction,float3(0,1,0))),up=cross(right,direction);
    float facing=dot(r,direction);
    float2 uv=float2(dot(r,right),dot(r,up))/max(facing,.001);
    float2 mask=1.-smoothstep(size-(float2)blur,size+(float2)blur,abs(uv));
    return mask.x*mask.y*smoothstep(0.,.15,facing);
}
float3 studioReflection(float3 r,float roughness) {
    float3 env=lerp(float3(.095,.080,.072),float3(.28,.27,.29),smoothstep(-.35,.8,r.y));
    float blur=.035+roughness*roughness*.85;
    env+=float3(3.3,3.1,2.9)*softbox(r,normalize(float3(3,5,-4)),float2(.32,.48),blur);
    env+=float3(.95,1.03,1.18)*softbox(r,normalize(float3(-4,2,-3)),float2(.19,.52),blur);
    env+=float3(1.7,1.45,1.8)*softbox(r,normalize(float3(-1,3,4)),float2(.18,.42),blur);
    return env;
}
float3 hairTangent(float3 p) {
    if(p.x>0.16 && p.y<1.775) {
        float t=clamp(lerp((1.7825-p.y)/0.5225,(-0.1825+p.x)/0.3825,max(motion.trail,0.)),0.,1.);
        return normalize(braidCenter(t+.005)-braidCenter(t-.005));
    }
    return normalize(float3(-.85,float(sign(p.x))*.6,-.15));
}
float2 jetInterval(float3 ro,float3 rd) {
    // Intersect only a tight local volume; most screen pixels do no plume work.
    float3 inv=1./(float3(sign(rd+float3(-1e-8,1e-8,-1e-8)))*max(abs(rd),(float3)(1e-7)));
    float3 a=(float3(0.08,-0.235,0.08)-ro)*inv;
    float3 b=(float3(-0.08,-0.0125,-0.08)-ro)*inv;
    float3 lo=min(a,b),hi=max(a,b);
    return float2(max(lo.x,max(lo.y,lo.z)),min(hi.x,min(hi.y,hi.z)));
}
float4 integrateJet(float3 ro,float3 rd,float2 interval,float solidDepth,float seed,float sampleOffset) {
    float begin=max(interval.x,0.),end=min(interval.y,solidDepth);
    if(end<=begin) return float4(0,0,0,1);
    float stepLength=(end-begin)/32.;
    float3 radiance=(float3)0; float transmission=1.;
    float time=frameGroup.time+seed;
    for(int i=0;i<32;i++) {
        float3 p=ro+rd*(begin+(float(i)+.5+sampleOffset*.65)*stepLength);
        p.y+=0.0275; // Gas begins at the bell lip, not inside the injector.
        float axial=max(-p.y,0.);
        float jetLength=0.12+0.08*motion.thrust;
        float tail=1.-smoothstep(jetLength*.35,jetLength,axial);
        float ignition=1.-smoothstep(-0.0025,0.0125,p.y);
        // Advected noise deforms the gas, with increasing breakup downstream.
        float3 flow=float3(p.x*-78.,axial*30.-time*9.,p.z*-78.+seed*7.);
        float turbulence=noise3(flow),fine=noise3(flow*1.93+float3(7,3,-5));
        float2 center=-.012*axial*float2(sin(axial*32.-time*8.),cos(axial*26.-time*11.));
        float width=0.0275*(1.-.30*clamp(axial/jetLength,0.,1.));
        width*=1.+.11*sin(axial*68.-.6*sin(time*4.));
        float radius=length(p.xz-center)/width;
        radius+=(turbulence-.5)*(.22+3.4*axial);
        float core=exp(-3.4*radius*radius)*tail*ignition;
        float sheath=exp(-1.05*radius*radius)*tail*ignition;
        sheath*=lerp(.60,1.35,turbulence)*lerp(.75,1.2,fine);
        // Pressure cells sit in a narrow fast core; the surrounding gas remains soft.
        float cells=pow(.5+.5*cos(axial*74.-.45*sin(time*3.)),7.);
        cells*=exp(-7.*radius*radius)*exp(-5.6*axial)*tail*ignition;
        float pulse=.94+.06*sin(time*17.);
        float3 emission=(float3(.008,.46,1.25)*sheath+float3(.24,2.3,3.8)*core
                       +float3(2.8,3.4,3.8)*cells)*pulse*28.*motion.thrust;
        float extinction=(.30*sheath+.70*core)*12.;
        float segment=exp(-extinction*stepLength);
        radiance+=transmission*emission*(1.-segment)/max(extinction,0.0002);
        transmission*=segment;
    }
    return float4(radiance,transmission);
}
float3 compositeJets(float3 col,float3 ro,float3 rd,float solidDepth,float2 sampleOffset) {
    if(JETS==0 || motion.thrust<.001) return col;
    float3 a=nozzleCoordinates(localPosition(ro,12));
    float3 ad=(nozzleCoordinates(localPosition(ro+rd*.5,12))-a)*2.;
    float3 b=nozzleCoordinates(localPosition(ro,13));
    float3 bd=(nozzleCoordinates(localPosition(ro+rd*.5,13))-b)*2.;
    float2 ia=jetInterval(a,ad),ib=jetInterval(b,bd);
    float4 left=integrateJet(a,ad,ia,solidDepth,0.,sampleOffset.x+sampleOffset.y*.5);
    float4 right=integrateJet(b,bd,ib,solidDepth,1.73,sampleOffset.x+sampleOffset.y*.5);
    if(ia.x<ib.x) { col=right.rgb+right.a*col; col=left.rgb+left.a*col; }
    else { col=left.rgb+left.a*col; col=right.rgb+right.a*col; }
    return col;
}
float3 render(float2 uv,float2 lightSample) {
    float3 ro,ww,uu,vv,rd;
    float focal=2.25;
    // The paired camera projects each pixel at its vertical field of view and placed aspect.
    // Geometry and rays share world units: +Y up, -Z forward, with the ground at Y=0.
    if(frameGroup.cameraFov>0.) {
        float3 forward=normalize(frameGroup.cameraTarget-frameGroup.cameraPosition);
        float3 right=normalize(cross(forward,normalize(frameGroup.cameraUp)));
        float3 up=cross(right,forward);
        focal=.5/tan(frameGroup.cameraFov*.5);
        ro=frameGroup.cameraPosition;
        rd=normalize(uv.x*right+uv.y*up+focal*forward);
    } else {
    float yaw=POSE==7?-.75:.22, pitch=.055;
    if(PACK_VIEW==1) { yaw=-2.65; pitch=.10; }
    if(AUTO_TURN==1) yaw+=frameGroup.time*.30;
    if(frameGroup.pointerPresses!=0) {
        yaw=(frameGroup.pointer.x/resolution.x-.5)*2.*PI;
        pitch=clamp((.5-frameGroup.pointer.y/resolution.y)*1.3,-.32,.72);
    }
    float3 target=float3(0,1.11,0);
    float cameraDistance=5.9;
    target.y+=.60*characterLift;
    cameraDistance+=0.3*smoothstep(0.,0.15,motion.lift);
    if(CLOSE_UP==1) { target=worldPosition(float3(0,1.82,-0.03),1); cameraDistance=2.4; }
    if(PACK_VIEW==1) { target=worldPosition(float3(0,1.315,0.215),0); cameraDistance=3.4; }
    ro=target+cameraDistance*float3(-(sin(yaw)*cos(pitch)),sin(pitch),-(cos(yaw)*cos(pitch)));
    ww=normalize(target-ro); uu=normalize(cross(ww,float3(0,1,0)));
    vv=cross(uu,ww);
    rd=normalize(uv.x*uu+uv.y*vv+focal*ww);
    }
    float3 col=lerp(float3(.49,.455,.415),float3(.34,.32,.31),smoothstep(-.5,.8,uv.y));
    float3 background=col;
    float t=0.,radius=0.,stepLength=0.,contour=100.; float2 h=float2(.5,0); bool hit=false;
    for(int i=0;i<MAX_STEPS;i++) {
        float candidate=t+stepLength;
        h=scene(ro+rd*candidate);
        float nextRadius=h.x*.78;
        // Keinert et al., Enhanced Sphere Tracing: a speculative segment must be
        // covered by overlapping unbounding spheres. Otherwise retry a safe step.
        // https://doi.org/10.2312/stag.20141233
        if(RELAXED_TRACE==1 && stepLength>radius*1.001 &&
           (nextRadius<0. || stepLength>radius+nextRadius)) {
            stepLength=radius;
            continue;
        }
        t=candidate;
        if(h.y>0. && floorMod(h.y,20.)<18.)
            contour=min(contour,max(h.x,0.)/max(candidate/(resolution.y*focal),0.0002));
        float hitEpsilon=0.00045*max(1.,t*0.32);
        // Resolve the small neck silhouette more closely than the broad armor.
        if(h.y==20.+SKIN && localPosition(ro+rd*t,1).y<1.74) hitEpsilon*=.20;
        if(h.x<hitEpsilon) { hit=true; break; }
        radius=max(nextRadius,0.000175);
        stepLength=radius*(RELAXED_TRACE==1?1.30:1.);
        if(t>40.) break;
    }
    if(hit) {
        float3 p=ro+rd*t;
        float4 gradient=surfaceGradient(p);
        float3 ng=gradient.xyz,n=ng,v=-rd;
        int part=int(floor(h.y/20.));
        float3 paintP=localPosition(p,part);
        h.y-=20.*float(part);
        Surface surf=material(h.y);
        float pixel=t/(resolution.y*focal);
        if(h.y==LILAC || h.y==IVORY) {
            finishArmor(p,ng,h.y,pixel,part,n,surf);
            float grazing=pow(clamp(1.-dot(n,v),0.,1.),3.5);
            float3 pearl=h.y==LILAC?float3(.43,.18,.36):float3(.90,.75,.51);
            surf.base=lerp(surf.base,pearl,grazing*.36*(1.-surf.metal));
            // The broad plate faces carry a restrained sky/ground value separation.
            surf.base*=.88+.12*smoothstep(-.55,.65,ng.y);
        }
        float skin=(h.y==SKIN || h.y==LIP)?1.:0.;
        if(skin>.5) {
            float2 cheekP=(float2(abs(paintP.x),paintP.y)-float2(0.1275,1.7975))/float2(0.055,0.036);
            float cheek=exp(-dot(cheekP,cheekP));
            surf.base=lerp(surf.base,float3(.39,.105,.047),cheek*.25);
            float mouthX=clamp(paintP.x,-0.0665,0.0665);
            float2 lipP=float2(-paintP.x/0.0575,(paintP.y-smileHeight(mouthX)+0.007)/0.006);
            float lip=exp(-dot(lipP,lipP))*smoothstep(0.18,0.235,-paintP.z);
            surf.base=lerp(surf.base,float3(.43,.158,.069),lip*.55);
            // Keep broad facial lighting smooth while preserving the modeled nose and lips.
            float3 guide=normalize((paintP-FACE_CENTER)/(FACE_RADII*FACE_RADII));
            guide=normalize(mul(guide,transpose(partFrame[part])));
            float nose=exp(-dot((paintP.xy-float2(0,1.81))/float2(0.0425,0.085),(paintP.xy-float2(0,1.81))/float2(0.0425,0.085)));
            float front=smoothstep(0.18,0.25,-paintP.z);
            if(h.y==SKIN) n=normalize(lerp(n,guide,.10*front*(1.-nose)*(1.-lip)));
        }
        float eyeOcclusion=1.; float3 eyeGlints=(float3)0;
        if(h.y==EYE) {
            eyeSurface(paintP,pixel,surf,eyeOcclusion,eyeGlints);
            // The cornea has its own optical normal; socket CSG must not flatten it.
            float3 cornea=normalize(eyeCoordinates(paintP)/(EYE_RADII*EYE_RADII));
            cornea.xy=mul(cornea.xy,rot(.055));
            cornea.xz=mul(cornea.xz,rot(.21));
            cornea.x*=-float(sign(paintP.x));
            n=normalize(mul(cornea,transpose(partFrame[part])));
        }
        float3 bleed;
        float ao=ambientOcclusion(p,ng,gradient.w,bleed);
        if(skin>.5) ao=lerp(ao,1.,.35);
        if(h.y==EYE) ao=max(ao,.70);
        float3 key=normalize(float3(3.,5.,-4.));
        float3 fill=normalize(float3(-4.,2.,-3.));
        float3 rim=normalize(float3(-1.,3.,4.));
        float3 lightRight=normalize(cross(key,float3(0,1,0)));
        float3 lightUp=cross(lightRight,key);
        float3 shadowDirection=normalize(key+.42*(lightSample.x*lightRight+lightSample.y*lightUp));
        float sh=shadow(p+ng*0.004,shadowDirection,gradient.w);
        col=directLight(surf,n,v,key,float3(3.15,3.,2.85),sh,skin);
        col+=directLight(surf,n,v,fill,float3(.65,.70,.82),ao,skin);
        col+=directLight(surf,n,v,rim,float3(1.7,1.45,1.9),ao,0.);
        float3 ambient=lerp(float3(.15,.105,.08),float3(.26,.25,.29),n.y*.5+.5);
        col+=surf.base*(1.-surf.metal)*ambient*ao;
        col+=surf.base*(1.-surf.metal)*bleed;
        if(skin>.5 && dot(n,rim)<-.1) {
            float3 localLight=normalize(mul(rim,partFrame[part]));
            float3 localNormal=normalize(mul(ng,partFrame[part]));
            float3 transmission=skinTransmission(paintP,localNormal,localLight);
            float backLight=pow(max(dot(-n,rim),0.),1.5);
            col+=float3(.36,.095,.028)*transmission*backLight;
        }
        if(JETS==1 && motion.thrust>.001 && skin<.5 && h.y!=EYE && h.y!=HAIR) {
            // Restrained nearby cyan bounce follows the two moving injectors.
            float bounce=0.;
            for(int i=0;i<2;i++) {
                float3 tip=nozzlePosition();
                float3 source=mul(tip-partOffset[12+i],transpose(partFrame[12+i]))-p;
                float reach=length(source);
                float facing=max(dot(n,source/max(reach,0.0005)),0.);
                bounce+=facing*exp(-reach*10.)/(0.0375+reach*reach)*.25;
            }
            col+=surf.base*float3(.010,.095,.14)*bounce*ao*motion.thrust;
        }
        float3 f0=lerp((float3)surf.specular,surf.base,surf.metal);
        float3 f=f0+(max((float3)(1.-surf.roughness),f0)-f0)*pow(clamp(1.-dot(n,v),0.,1.),5.);
        float specAO=clamp(pow(ao,1.+surf.roughness),0.,1.);
        col+=studioReflection(reflect(-v,n),surf.roughness)*f*specAO*(h.y==EYE?.08:.75);
        if(h.y==EYE) col+=eyeGlints;
        if(h.y==HAIR && (abs(paintP.x)>0.155 || paintP.y>1.97)) {
            float3 tangent=normalize(mul(hairTangent(paintP),transpose(partFrame[part])));
            float3 halfVector=normalize(key+v);
            // Two shifted, bounded sheen lobes approximate surface and internal reflection.
            float th=dot(tangent,halfVector);
            float primary=pow(max(1.-square(th+.05),0.),65.);
            float secondary=pow(max(1.-square(th-.10),0.),18.);
            float strand=.90+.10*sin(dot(paintP,float3(-290.,106.,-178.)));
            col+=(float3(.044,.041,.038)*primary+float3(.037,.025,.014)*secondary)
                  *strand*max(dot(n,key),0.)*sh;
        }
        if(h.y==CYAN) {
            // Dark lens edges and a pale luminous center retain the optic's volume.
            float facing=max(dot(n,v),0.);
            float core=pow(facing,7.);
            col+=float3(.006,.27,.39)+float3(.11,1.2,1.65)*pow(facing,3.);
            col+=float3(.7,1.,1.)*core*.65;
        }
        if(h.y<.5) {
            // Broad contact grounding, supplementing ray-marched shadows.
            float contact=exp(-11.2*dot(p.xz,p.xz)-8.*motion.lift);
            col*=1.-.22*contact;
            col=lerp(col,background,smoothstep(7.,22.5,t));
        }
    }
    if(INK==1 && (!hit || h.y<.5)) {
        // Only missed/grazed character surfaces can ink the floor or background.
        float ink=1.-smoothstep(.15,.55,contour);
        col=lerp(col,float3(.025,.018,.032),ink*.8);
    }
    return compositeJets(col,ro,rd,hit?t:40.,lightSample);
}
float4 shade(float2 pixel) {
    float blinkPhase=(frac((frameGroup.time+1.1)/5.1)-.50)/.023;
    float blink=exp(-square(square(blinkPhase)));
    eyeOpen=ANIMATE_FACE==1?1.-.98*blink:1.;
    preparePose();
    preparePack();
    prepareHair();
    prepareBraid();
    float3 color=(float3)0;
    for(int y=0;y<AA;y++) for(int x=0;x<AA;x++) {
        float2 offset=(float2(float(x),float(y))+.5)/float(AA)-.5;
        float2 uv=((pixel+offset)/resolution-.5)*float2(aspect,1.);
        color+=render(uv,offset);
    }
    color/=float(AA*AA);
    // Tone mapping follows linear-light integration, including illustrated eye glints.
    color=(color*(2.51*color+.03))/(color*(2.43*color+.59)+.14);
    color=pow(max(color,0.),(float3)(1./2.2));
    float2 uv=(pixel/resolution-.5)*float2(aspect,1.);
    color*=1.-.14*dot(uv,uv);
    color+=(hash31(float3(pixel,17.))-.5)/255.;
    return float4(color,1.);
}

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    uint width;
    uint height;

    output.GetDimensions(width, height);
    if ((id.x >= width) || (id.y >= height)) {
        return;
    }
    resolution = float2(width, height);
    aspect = frameGroup.placedExtent.x / frameGroup.placedExtent.y;
    // Pixel centers with y growing up the image, as the shading functions above expect.
    output[id.xy] = shade(float2(id.x + 0.5, height - (id.y + 0.5)));
}
