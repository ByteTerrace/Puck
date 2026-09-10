// MOTH - procedural SDF study of 01-character-model-sheet.png.
// Swept shoulders, curved boots and surface finish: 02-face-and-armor.png.
// Paste this entire file into Shadertoy's Image tab. No channels required.
// Drag mouse: orbit. Release: hold view. Set AUTO_TURN to 1 for a turntable.
// Approximate sculpt, not a mesh reconstruction. Front is +Z; units are artistic.
// CLOSE_UP: face/shoulder framing. AA: 1 = fast, 2 = four samples per pixel.
// RUN_POSE: 0 = neutral sculpt, 1 = a frozen running key pose with a trailing braid.
// ANIMATE_FACE adds a brief blink every 5.1 seconds; set to 0 for a still study.
// Plates use rigid joint transforms; surface detail follows each piece in either pose.
// Refractive procedural eyes, swept hair, GGX paint and art-directed skin normals.
// Secondary rays use simplified head geometry; eyelid occlusion is shaded locally.
// AA samples also sample the studio light; tone mapping follows linear averaging.
// PACK_VIEW frames the curved flight pods. JETS toggles depth-clipped volumetric exhaust.
// WEAR: 0 = fresh paint, 1 = restrained edge chips, scratches and contact abrasion.
// Jet flow animates with iTime; no noise textures, buffers or opaque flame meshes.
// The braid exits beside the right cheek through the front opening in both poses.
#define AUTO_TURN 0
#define CLOSE_UP 0
#define PACK_VIEW 0
#define JETS 1
#define WEAR 1
#define RUN_POSE 0
#define ANIMATE_FACE 1
#define AA 2
#define MAX_STEPS 220
#define SHADOW_STEPS 96

const float PI = 3.14159265;
// Base material IDs. Hits add 20 times the rigid part index for local surface details.
const float LILAC=1., IVORY=2., JOINT=3., SKIN=4., HAIR=5.;
const float GOLD=6., CYAN=7., EYE=8., STEEL=9.;
const float OCHRE=11.;
const float LIP=12., MOUTH=13.;
float eyeOpen;

mat2 rot(float a) { float c=cos(a),s=sin(a); return mat2(c,-s,s,c); }
float ell(vec3 p, vec3 r) {
    // Conservative distance bound, including at the ellipsoid center.
    return (length(p/r)-1.)*min(r.x,min(r.y,r.z));
}
float box(vec3 p, vec3 b, float r) {
    vec3 q=abs(p)-b;
    return length(max(q,0.))+min(max(q.x,max(q.y,q.z)),0.)-r;
}
float cap(vec3 p, vec3 a, vec3 b, float r) {
    vec3 v=p-a,w=b-a;
    return length(v-w*clamp(dot(v,w)/dot(w,w),0.,1.))-r;
}
float cylZ(vec3 p, float r, float h) {
    vec2 d=vec2(length(p.xy)-r,abs(p.z)-h);
    return min(max(d.x,d.y),0.)+length(max(d,0.));
}
float cylX(vec3 p, float r, float h) { return cylZ(p.zyx,r,h); }
void add(inout vec2 h, float d, float m) { if(d<h.x) h=vec2(d,m); }
float smoothUnion(float a,float b,float k) {
    float t=clamp(.5+.5*(b-a)/k,0.,1.);
    return mix(b,a,t)-k*t*(1.-t);
}
float smoothIntersection(float a,float b,float k) {
    return -smoothUnion(-a,-b,k);
}
vec3 bezier(vec3 a,vec3 b,vec3 c,float t) {
    return mix(mix(a,b,t),mix(b,c,t),t);
}
vec4 hairNodes[52];
void prepareHair() {
    // Sample the authored curves once per fragment; march only the cached sweeps.
    for(int lock=0;lock<4;lock++) {
        vec3 a,b,c; float width;
        if(lock==0) { a=vec3(.105,4.18,.285); b=vec3(-.105,4.17,.57); c=vec3(-.44,3.80,.36); width=.133; }
        else if(lock==1) { a=vec3(.09,4.18,.286); b=vec3(.29,4.11,.53); c=vec3(.45,3.88,.33); width=.088; }
        else if(lock==2) { a=vec3(-.33,3.925,.285); b=vec3(-.435,3.72,.39); c=vec3(-.365,3.55,.325); width=.074; }
        else { a=vec3(.355,3.93,.23); b=vec3(.425,3.76,.315); c=vec3(.348,3.60,.26); width=.067; }
        for(int i=0;i<13;i++) {
            float t=float(i)/12.;
            // Broad roots join the hair cap, then taper into swept, thin tips.
            float taper=(.80+.40*sin(PI*t))*(1.-smoothstep(.35,1.,t));
            hairNodes[lock*13+i]=vec4(bezier(a,b,c,t)*vec3(1,1,3.1),.008+width*taper);
        }
    }
}
float hairSculpt(vec3 p) {
    p*=vec3(1,1,3.1);
    float d=10.;
    for(int i=0;i<48;i++) {
        int node=i+i/12;
        vec4 a=hairNodes[node],b=hairNodes[node+1];
        vec3 v=b.xyz-a.xyz;
        float t=clamp(dot(p-a.xyz,v)/dot(v,v),0.,1.);
        float strand=length(p-mix(a.xyz,b.xyz,t))-mix(a.w,b.w,t);
        d=smoothUnion(d,strand,.014);
    }
    return d*.27;
}
vec3 eyeCoordinates(vec3 p) {
    p.x=abs(p.x); p-=vec3(.174,3.743,.421);
    p.xz=rot(-.21)*p.xz; p.xy=rot(.055)*p.xy;
    return p;
}
const vec3 EYE_RADII=vec3(.145,.145,.098);
float eyeFront(vec2 p) { return .098*sqrt(max(1.-dot(p,p)/(.145*.145),.0001)); }
vec2 eyelidHeights(float x) {
    float u=x/.110,arch=pow(max(1.-u*u,0.),.65);
    return vec2(.083*arch+.012*u,-.071*arch+.012*u)*eyeOpen;
}
float eyeOpening(vec3 q) {
    vec2 lids=eyelidHeights(q.x);
    return max(max(q.y-lids.x,lids.y-q.y),abs(q.x)-.110)*.60;
}
float lidDistance(vec3 q,bool upper) {
    float x=clamp(q.x,-.108,.108);
    vec2 heights=eyelidHeights(x);
    float y=upper?heights.x:heights.y;
    float z=eyeFront(vec2(x,y));
    float radius=upper?(.010+.005*smoothstep(-.07,.12,x)):.0035;
    return (length(q-vec3(x,y,z))-radius)*.30;
}
const vec3 FACE_CENTER=vec3(0,3.725,.235);
const vec3 FACE_RADII=vec3(.400,.375,.295);
float faceWidth(float y) {
    float jaw=mix(.76,1.,smoothstep(3.38,3.65,y));
    return .400*jaw*(1.-.045*smoothstep(3.90,4.10,y));
}
float smileHeight(float x) { return 3.496+1.75*x*x+.018*x; }
float faceOffset(vec2 p) {
    vec2 cheek=(vec2(abs(p.x),p.y)-vec2(.245,3.605))/vec2(.14,.112);
    vec2 muzzle=(p-vec2(0,3.515))/vec2(.16,.072);
    vec2 chin=(p-vec2(0,3.435))/vec2(.16,.077);
    vec2 lip=vec2(p.x/.105,(p.y-smileHeight(clamp(p.x,-.13,.13))+.014)/.012);
    return .042*exp(-dot(cheek,cheek))+.022*exp(-dot(muzzle,muzzle))
          +.025*exp(-dot(chin,chin))+.009*exp(-dot(lip,lip));
}
float faceFront(vec2 p) {
    vec2 q=vec2(p.x/faceWidth(p.y),(p.y-FACE_CENTER.y)/FACE_RADII.y);
    return FACE_CENTER.z+faceOffset(p)+FACE_RADII.z*sqrt(max(1.-dot(q,q),0.));
}
float faceSculpt(vec3 p) {
    // One continuous deformed surface gives the cheeks and muzzle their volume.
    vec3 q=p-FACE_CENTER; q.z-=faceOffset(p.xy);
    float d=ell(q,vec3(faceWidth(p.y),FACE_RADII.yz));
    vec3 s=p; s.x=abs(s.x);
    d=smoothUnion(d,cap(p,vec3(0,3.692,.508),vec3(0,3.649,.530),.014),.041);
    d=smoothUnion(d,ell(p-vec3(0,3.610,.551),vec3(.048,.028,.040)),.031);
    d=smoothUnion(d,ell(s-vec3(.032,3.601,.538),vec3(.023,.017,.023)),.021);
    return d*.70;
}
float skinGeometry(vec3 p) {
    // The throat continues into the chest opening and blends beneath the chin.
    float neck=ell(p-vec3(0,3.265,.190),vec3(.105,.185,.100));
    float skin=smoothUnion(faceSculpt(p),neck,.035);
    vec3 ear=p; ear.x=abs(ear.x);
    return min(skin,ell(ear-vec3(.365,3.645,.22),vec3(.048,.090,.055)));
}
float edgePlane(vec2 p,vec2 a,vec2 b) {
    vec2 e=b-a;
    return dot(p-a,vec2(-e.y,e.x))/length(e);
}
float fiveSides(vec2 p,vec2 a,vec2 b,vec2 c,vec2 d,vec2 e) {
    // Clockwise convex profile; half-plane distances are conservative at corners.
    float f=smoothIntersection(edgePlane(p,a,b),edgePlane(p,b,c),.018);
    f=smoothIntersection(f,edgePlane(p,c,d),.018);
    f=smoothIntersection(f,edgePlane(p,d,e),.018);
    return smoothIntersection(f,edgePlane(p,e,a),.018);
}
float roundedExtrusion(float profile,float depth,float bevel) {
    vec2 q=vec2(profile,depth)+bevel;
    return min(max(q.x,q.y),0.)+length(max(q,0.))-bevel;
}
float shoulderProfile(vec2 p) {
    return fiveSides(p,vec2(-.12,.10),vec2(.08,.20),vec2(.29,.10),
                     vec2(.48,-.45),vec2(.12,-.24));
}
float shoulderPlate(vec3 q) {
    // A thin tip and a domed cross-section, with a broad face for the paint.
    float radius=.31*(1.-.50*smoothstep(.05,.45,-q.y));
    vec2 sectionP=pow(abs(vec2((q.x+.02)/.50,q.z/radius)),vec2(2.6));
    float section=(pow(sectionP.x+sectionP.y,1./2.6)-1.)*radius;
    return roundedExtrusion(shoulderProfile(q.xy),section,.018)*.7;
}
vec2 shinRadii(float y) {
    float flare=clamp((1.41-y)/1.12,0.,1.);
    return vec2(.23,.21)+vec2(.155,.23)*flare
         +vec2(.035,.040)*sin(PI*flare);
}
float curvedSection(vec2 p,vec2 r) {
    // Between an ellipse and a rounded rectangle: broad, gently curved plate faces.
    vec2 q=pow(abs(p)/r,vec2(2.6));
    return (pow(q.x+q.y,1./2.6)-1.)*min(r.x,r.y);
}
float ankleOpening(vec2 p) {
    float arch=(length(vec2(p.x,p.y-.27)/vec2(.302,.30))-1.)*.30;
    return max(.285-p.y,-arch);
}
float footOutline(vec3 q) {
    float outline=curvedSection(vec2(q.x,q.z-.17),vec2(.33,.48));
    float toeCorners=(abs(q.x)+.50*(q.z-.48)-.30)/1.118;
    return smoothIntersection(outline,toeCorners,.018);
}
vec3 gauntletCoordinates(vec3 p) {
    p-=vec3(.92,2.10,.015); p.xy=rot(-.23)*p.xy;
    return p;
}
float gauntletShell(vec3 q) {
    // A bowed exterior with a high elbow point and a rounded wrist cheek.
    vec3 a=q-vec3(.085,.015,.015);
    a.x+=.14*a.y+.18*a.y*a.y;
    float shell=ell(a,vec3(.265,.425,.275))*.78;
    float bevel=max(.8*a.x+.15*a.y+.58*abs(a.z)-.245,
                    max(-a.y+.28*a.x-.37,.9*a.y+.25*a.x-.34));
    shell=smoothIntersection(shell,bevel,.024);
    shell=smoothIntersection(shell,abs(a.z)-(.214+.10*a.y),.016);
    return smoothIntersection(shell,-q.x-.035-.25*q.y,.025);
}
vec3 podCoordinates(vec3 p) {
    p.x=abs(p.x); p-=vec3(.285,2.73,-.415);
    // The centerline bows outward and aft, then curls back toward the exhaust.
    float u=clamp((.50-p.y)/1.05,0.,1.);
    p.x-=.045*u+.075*sin(PI*u);
    p.z+=.020+.185*sin(PI*u)+.12*u;
    return p;
}
float podShell(vec3 q) {
    // A rounded leaf loft, with a narrow root and a swept, asymmetric lower lip.
    float width=.25+.035*clamp(.5-q.y,0.,1.);
    float shell=ell(q-vec3(0,.01,0),vec3(width,.60,.18));
    float lip=max(q.y-.505+.25*q.x,-q.y-.445-.65*q.x+.20*q.z);
    return smoothIntersection(shell,lip,.045)*.57;
}
float podBand(vec3 q) { return q.y+.38*q.x+.80*q.x*q.x+.12*q.z; }
vec3 nozzleCoordinates(vec3 p) {
    // A rigid exhaust frame, shared by the physical nozzle and the volume renderer.
    p-=vec3(.37,2.245,-.60);
    p.xy=rot(.10)*p.xy;
    p.yz=rot(.28)*p.yz;
    return p;
}
mat3 partFrame[12];
vec3 partOffset[12];
void rotateFrame(inout mat3 frame,inout vec3 offset,vec3 pivot,float angle) {
    float c=cos(angle),s=sin(angle);
    mat3 rotation=mat3(vec3(1,0,0),vec3(0,c,-s),vec3(0,s,c));
    frame=rotation*frame;
    offset=rotation*(offset-pivot)+pivot;
}
void preparePose() {
    // Cache inverse rigid frames once per fragment, outside all distance queries.
    for(int i=0;i<12;i++) {
        mat3 frame=mat3(1.); vec3 offset=vec3(0);
        if(RUN_POSE==1) rotateFrame(frame,offset,vec3(0,2.15,0),.13);
        bool otherSide=(i==4 || i==5 || i==8 || i==9 || i==11);
        if(i==1) {
            if(RUN_POSE==1) rotateFrame(frame,offset,vec3(0,3.15,0),-.10);
            mat3 scale=mat3(vec3(1./.88,0,0),vec3(0,1./.88,0),vec3(0,0,1./.90));
            frame=scale*frame;
            offset=scale*(offset-vec3(0,3.2,0))+vec3(0,3.2,0);
        } else if(i>=2) {
            if(otherSide) {
                mat3 mirror=mat3(vec3(-1,0,0),vec3(0,1,0),vec3(0,0,1));
                frame=mirror*frame; offset.x=-offset.x;
            }
            if(RUN_POSE==1) {
                if(i<=5) {
                    rotateFrame(frame,offset,vec3(.29,2.055,0),otherSide?.65:-.75);
                    if(i==3 || i==5) rotateFrame(frame,offset,vec3(.38,1.49,0),otherSide?.90:.38);
                } else if(i<=9) {
                    rotateFrame(frame,offset,vec3(.59,2.90,0),otherSide?1.38:-.75);
                    if(i==7 || i==9) rotateFrame(frame,offset,vec3(.78,2.35,.01),otherSide?-.15:-1.20);
                } else rotateFrame(frame,offset,vec3(.59,2.90,0),.40*(otherSide?1.38:-.75));
            }
        }
        partFrame[i]=frame; partOffset[i]=offset;
    }
}
vec3 localPosition(vec3 p,int part) { return partFrame[part]*p+partOffset[part]; }
vec3 braidCenter(float t) {
    // The visible root exits the front aperture beside her right cheek.
    vec3 root=vec3(-.365,3.565,.325);
    if(RUN_POSE==0) return bezier(root,vec3(-.54,3.13,.62),vec3(-.51,2.52,.51),t);
    // Clear the front rim and shoulder before the free end sweeps aft in motion.
    vec3 a=mix(root,vec3(-.57,3.24,.62),t);
    vec3 b=mix(vec3(-.57,3.24,.62),vec3(-.95,3.11,.23),t);
    vec3 c=mix(vec3(-.95,3.11,.23),vec3(-1.22,3.05,-.68),t);
    return mix(mix(a,b,t),mix(b,c,t),t);
}
vec3 braidStrand(float t,float strand) {
    vec3 tangent=normalize(braidCenter(t+.001)-braidCenter(t-.001));
    vec3 u=normalize(cross(tangent,vec3(0,0,1))),v=cross(tangent,u);
    // A figure-eight cross-section alternates the over/under crossings of a flat plait.
    float phase=t*6.*PI+strand*2.*PI/3.;
    return braidCenter(t)+(u*cos(phase)*.056+v*sin(2.*phase)*.050)*(1.-.30*t);
}
vec4 braidNodes[75];
vec3 braidTip,braidDirection;
void prepareBraid() {
    for(int strand=0;strand<3;strand++) for(int i=0;i<25;i++) {
        float t=float(i)/24.;
        braidNodes[strand*25+i]=vec4(braidStrand(t,float(strand)),.050-.010*t);
    }
    braidTip=braidCenter(1.);
    braidDirection=normalize(braidTip-braidCenter(.97));
}

vec2 helmetShell(vec3 p) {
    // The same open-bottom shell is used by camera, shadow and occlusion rays.
    vec3 q=p-vec3(0,3.77,-.035);
    float outer=ell(q,vec3(.655,.685,.55));
    float inner=ell(q-vec3(0,-.01,.055),vec3(.555,.587,.49));
    float width=.505*(.84+.16*smoothstep(-.50,-.10,q.y));
    vec2 opening=(q.xy-vec2(0,-.045))/vec2(width,.548);
    float cut=max((length(opening)-1.)*width,.07-q.z);
    float chinHeight=3.34-.055*smoothstep(.20,.55,abs(p.x));
    float throatCut=max(p.y-chinHeight,.015-p.z);
    float d=max(max(max(outer,-inner),-cut),-throatCut);
    float hoodMaterial=(cut<.090 && q.z>.075)?IVORY:LILAC;
    if(throatCut<.018 && p.z>.015) hoodMaterial=IVORY;
    if(-inner>max(max(outer,-cut),-throatCut)-.001) hoodMaterial=JOINT;
    if(q.z<-.24 && q.y<-.30-.45*q.x*q.x) hoodMaterial=JOINT;
    return vec2(d,hoodMaterial);
}
vec2 headScene(vec3 p) {
    vec3 q,s=p; s.x=abs(s.x);
    float d;
    vec2 h=vec2(10.,HAIR);
    float bound=box(p-vec3(-.24,3.34,-.28),vec3(1.15,1.16,1.25),.025);
    if(bound>.20) return vec2(bound,-1.);
    vec2 hood=helmetShell(p); add(h,hood.x,hood.y);
    add(h,cylX(s-vec3(.626,3.70,-.035),.218,.033)-.007,IVORY);
    add(h,cylX(s-vec3(.672,3.70,-.035),.159,.011)-.007,LILAC);
    add(h,cylX(s-vec3(.696,3.70,-.035),.100,.005)-.004,GOLD);
    add(h,cylX(s-vec3(.708,3.70,-.035),.075,.005)-.003,LILAC);
    // Separate cheek guards extend the hood's shaped ivory edge below each ear.
    q=s-vec3(.465,3.47,.240);
    q.z+=.16*q.y;
    float guard=fiveSides(q.xy,vec2(-.02,.15),vec2(.09,.105),vec2(.085,-.04),
                         vec2(-.145,-.125),vec2(-.175,-.060));
    add(h,roundedExtrusion(guard,abs(q.z)-.052,.018),IVORY);
    add(h,cap(s,vec3(.46,3.57,.13),vec3(.29,3.35,.19),.043),JOINT);

    // Continuous cheeks, button nose and lip volume, with inset almond eye openings.
    float face=skinGeometry(p);
    q=eyeCoordinates(p);
    float socket=max(eyeOpening(q),-.025-q.z);
    face=smoothIntersection(face,-socket,.006);
    float mouthX=clamp(p.x,-.132,.132);
    float smile=smileHeight(mouthX);
    float mouthWidth=max(1.-pow(mouthX/.133,2.),0.);
    float lipZ=faceFront(vec2(mouthX,smile));
    float mouth=max(max(abs(p.y-smile)-(.002+.003*mouthWidth),abs(p.x)-.133),lipZ-.012-p.z);
    float faceWithMouth=max(face,-mouth);
    add(h,faceWithMouth,(-mouth>face)?MOUTH:SKIN);
    if(h.y==SKIN && p.z>.565) {
        float nostril=length((vec2(s.x,p.y)-vec2(.028,3.591))/vec2(.008,.0035));
        if(nostril<1.) h.y=MOUTH;
    }

    // Embedded eyeballs. Iris depth and corneal reflections are evaluated at the hit.
    q=eyeCoordinates(p);
    add(h,max(ell(q,EYE_RADII),eyeOpening(q)),EYE);
    add(h,lidDistance(q,true),HAIR);
    add(h,lidDistance(q,false),SKIN);
    vec3 lashRoot=vec3(.097,.036*eyeOpen,eyeFront(vec2(.097,.036*eyeOpen)));
    vec3 lashTip=vec3(.134,.043*eyeOpen,eyeFront(vec2(.114,.012))-.010);
    vec3 lashVector=lashTip-lashRoot;
    float lashT=clamp(dot(q-lashRoot,lashVector)/dot(lashVector,lashVector),0.,1.);
    add(h,(length(q-mix(lashRoot,lashTip,lashT))-mix(.012,.0015,lashT))*.75,HAIR);
    vec3 tear=vec3(-.102,-.012*eyeOpen,eyeFront(vec2(-.102,-.012)));
    add(h,ell(q-tear,vec3(.009,.005*eyeOpen+.001,.006)),LIP);
    float browT=clamp((s.x-.069)/.226,0.,1.);
    vec3 brow=bezier(vec3(.069,3.915,.516),vec3(.165,3.953,.524),vec3(.295,3.918,.432),browT);
    add(h,(length((s-brow)*vec3(1,1,1.9))-(.014+.011*sin(PI*browT)))*.40,HAIR);

    // The fringe is built from flattened, tapered curve sweeps over the scalp.
    q=p-vec3(0,3.80,.19);
    d=ell(q,vec3(.445,.43,.35));
    d=smoothIntersection(d,max(3.91-p.y,p.z-.335),.025);
    add(h,d,HAIR);
    add(h,hairSculpt(p),HAIR);
    // Three interwoven strands share the front root in the neutral and running poses.
    for(int i=0;i<72;i++) {
        int node=i+i/24;
        add(h,cap(p,braidNodes[node].xyz,braidNodes[node+1].xyz,braidNodes[node].w),HAIR);
    }
    vec3 tip=braidTip;
    vec3 tangent=braidDirection;
    vec3 tie=p-tip;
    float tieY=dot(tie,tangent);
    vec3 radial=tie-tangent*tieY;
    vec2 band=vec2(length(radial)-.073,abs(tieY)-.021);
    add(h,min(max(band.x,band.y),0.)+length(max(band,0.))-.006,GOLD);
    vec3 side=normalize(cross(tangent,vec3(0,0,1))),depth=cross(tangent,side);
    vec3 tuft=vec3(dot(tie,side),tieY-.095,dot(tie,depth));
    tuft.x-=.013*sin(clamp(tieY/.21,0.,1.)*PI);
    add(h,ell(tuft,vec3(.048,.105,.034)),HAIR);

    return h;
}

vec2 upperLegScene(vec3 p) {
    vec2 h=vec2(10.,JOINT);
    vec3 q,s=p;
    float d;
    float bound=box(p-vec3(.38,1.90,.04),vec3(.29,.46,.30),.02);
    if(bound>.18) return vec2(bound,-1.);
    // Symmetric limbs. Armor stays separate to retain joint gaps.

    add(h,cap(s,vec3(.27,2.05,0),vec3(.37,1.48,0),.19),JOINT);
    q=s-vec3(.365,1.84,.015); q.xy=rot(-.12)*q.xy;
    d=smoothIntersection(ell(q,vec3(.235,.32,.228)),-q.x-.022,.018);
    d=smoothIntersection(d,abs(q.z)-(.185-.10*q.y),.022);
    d=smoothIntersection(d,q.x-.18-.06*q.y,.022);
    add(h,d,LILAC);
    q=s-vec3(.445,2.115,.04); q.xy=rot(-.40)*q.xy;
    float hip=fiveSides(q.xy,vec2(-.09,.18),vec2(.08,.18),vec2(.13,.035),
                       vec2(.05,-.20),vec2(-.12,-.04));
    d=roundedExtrusion(hip,abs(q.z)-.19+.30*q.y*q.y,.024)*.8;
    add(h,d,(q.z>.14)?IVORY:LILAC);
    return h;
}

vec2 lowerLegScene(vec3 p) {
    vec2 h=vec2(10.,JOINT);
    vec3 q,s=p;
    float d;
    float bound=box(p-vec3(.41,.77,.075),vec3(.45,.90,.62),.02);
    if(bound>.18) return vec2(bound,-1.);
    add(h,ell(s-vec3(.38,1.49,0),vec3(.19,.17,.195)),JOINT);
    add(h,cylX(s-vec3(.575,1.49,0),.092,.017)-.008,JOINT);
    q=s-vec3(.38,1.49,.21); q.xy=rot(-.12)*q.xy;
    float knee=fiveSides(q.xy,vec2(-.12,.16),vec2(.115,.16),vec2(.15,-.02),
                        vec2(.06,-.18),vec2(-.105,-.135));
    add(h,roundedExtrusion(knee,abs(q.z)-.095+.5*q.x*q.x,.025)*.9,IVORY);
    // Continuously flared shin shell, with a real arched ankle cutout.
    q=s-vec3(.4,0,.025);
    q.z-=.035*sin(PI*clamp((1.41-q.y)/1.12,0.,1.));
    vec2 radii=shinRadii(q.y);
    float side=curvedSection(q.xz,radii);
    float ankleCut=ankleOpening(q.xy);
    float kneeSeat=(length(vec2(q.x,q.y-1.46)/vec2(.145,.15))-1.)*.145;
    float shin=smoothIntersection(side,max(max(q.y-1.41+.10*q.z,ankleCut),-kneeSeat),.025)*.7;
    add(h,shin,LILAC);
    // The ivory trim follows the arch all the way around the boot.
    add(h,max(shin-.003,-ankleCut-.095),IVORY);
    // A narrow ivory side panel follows the bowed shin, tapering toward the knee.
    float cheek=fiveSides(q.zy,vec2(-.13,1.25),vec2(.07,1.25),vec2(.14,.96),
                          vec2(-.045,.53),vec2(-.26,.74));
    add(h,max(shin-.008,max(cheek,.20-q.x)),IVORY);
    add(h,ell(s-vec3(.4,.38,.015),vec3(.21,.23,.225)),JOINT);
    // Sculpted instep and rounded, broad toe, resting on a flat rubber sole.
    q=s-vec3(.4,0,0);
    float outline=footOutline(q);
    outline=smoothIntersection(outline,(q.z+.45*q.y-.73)/1.096,.035);
    float crown=1.-.75*clamp(pow(q.x/.33,2.),0.,1.);
    float top=.225+.34*exp(-pow((q.z-.15)/.36,2.))*crown;
    float shoe=roundedExtrusion(outline,max(.055-q.y,q.y-top),.03)*.45;
    float toe=q.z-(.46-.12*pow(q.x/.33,2.));
    add(h,shoe,(toe>0. || q.z+.45*q.y>.69 || q.y<.15 || q.z<-.17)?LILAC:IVORY);
    add(h,roundedExtrusion(outline+.003,abs(q.y-.048)-.036,.015),JOINT);
    add(h,cylX(s-vec3(.772,.365,.005),.114,.025)-.008,JOINT);
    add(h,cylX(s-vec3(.804,.365,.005),.078,.012)-.006,LILAC);
    add(h,cylZ(s-vec3(.4,.31,-.345),.14,.032)-.012,JOINT);
    add(h,cylZ(s-vec3(.4,.31,-.383),.106,.014)-.008,LILAC);
    add(h,cylZ(s-vec3(.4,.31,-.405),.086,.016),JOINT);
    add(h,ell(s-vec3(.4,.31,-.421),vec3(.070,.070,.026)),CYAN);

    return h;
}

vec2 upperArmScene(vec3 p) {
    vec2 h=vec2(10.,JOINT);
    vec3 q,s=p;
    float d;
    float bound=box(p-vec3(.68,2.61,0),vec3(.30,.49,.23),.02);
    if(bound>.18) return vec2(bound,-1.);
    // Upper arm, elbow and gently splayed forearm.
    add(h,cap(s,vec3(.59,2.90,0),vec3(.77,2.39,0),.16),JOINT);
    add(h,ell(s-vec3(.78,2.35,.01),vec3(.17,.16,.175)),JOINT);
    return h;
}

vec2 forearmScene(vec3 p) {
    vec2 h=vec2(10.,JOINT);
    vec3 q,s=p;
    float d;
    float bound=box(p-vec3(.96,2.02,.03),vec3(.42,.62,.40),.02);
    if(bound>.18) return vec2(bound,-1.);
    q=gauntletCoordinates(p);
    vec2 barrelRadii=vec2(.177,.185)+.055*smoothstep(-.32,.22,q.y);
    float barrelEnds=max(q.y-.31+.28*q.x,-q.y-.32+.12*q.x);
    d=roundedExtrusion(curvedSection(q.xz+vec2(.025,0),barrelRadii),barrelEnds,.025)*.8;
    add(h,d,LILAC);
    add(h,gauntletShell(q),IVORY);
    vec3 lens=q-vec3(.24,-.235,.18); lens.xz=rot(-.86)*lens.xz;
    add(h,cylZ(lens,.119,.025)-.012,IVORY);
    add(h,cylZ(lens-vec3(0,0,.031),.091,.016)-.006,JOINT);
    add(h,ell(lens-vec3(0,0,.052),vec3(.060,.060,.023)),CYAN);
    // Four curled fingers extend below the palm and remain readable from the rear.
    add(h,ell(s-vec3(1.035,1.65,.05),vec3(.14,.13,.12)),JOINT);
    for(int i=0;i<4;i++) {
        float f=float(i);
        vec3 c=vec3(.925+.071*f,1.49+.025*abs(f-1.5),.12);
        add(h,cap(s,c,c+vec3(.015,.115,.045),.041),JOINT);
        add(h,cap(s,c+vec3(.012,.015,-.07),c+vec3(.02,.06,.035),.040),JOINT);
    }
    add(h,cap(s,vec3(.90,1.71,.17),vec3(.865,1.60,.20),.061),JOINT);

    return h;
}

vec2 shoulderScene(vec3 p) {
    vec2 h=vec2(10.,JOINT);
    vec3 q,s=p;
    float d;
    float bound=box(p-vec3(.77,2.93,.015),vec3(.42,.37,.36),.02);
    if(bound>.18) return vec2(bound,-1.);
    // Swept overlapping plates. The upper saddle sits behind the long outer blade.
    q=s-vec3(.61,3.10,-.025);
    q.y*=1.25;
    float saddle=ell(q-vec3(.04,-.05,0),vec3(.30,.21,.29));
    float saddleInside=ell(q-vec3(.04,-.085,0),vec3(.245,.165,.235));
    saddle=smoothIntersection(saddle,max(-saddleInside,-.035-q.y-.32*q.x),.012);
    add(h,saddle*.85,LILAC);
    q=s-vec3(.62,3.00,.015);
    d=shoulderPlate(q);
    add(h,d,LILAC);
    float stripe=(q.y+.43*q.x+.025)/1.09;
    add(h,max(d-.002,abs(stripe)-.037),IVORY);
    add(h,cylZ(s-vec3(.60,2.98,.322),.146,.037)-.012,LILAC);
    add(h,cylZ(s-vec3(.60,2.98,.373),.059,.010)-.007,IVORY);

    return h;
}

vec2 torsoScene(vec3 p) {
    vec2 h=vec2(10.,JOINT);
    vec3 q,s=p;
    float d;
    float bound=box(p-vec3(0,2.53,-.16),vec3(.73,.84,.79),.02);
    if(bound>.18) return vec2(bound,-1.);
    // A compact breastplate with a curved ivory inset and a beveled lower edge.
    add(h,ell(p-vec3(0,2.58,0),vec3(.41,.57,.245)),JOINT);
    float chest=ell(p-vec3(0,2.79,.025),vec3(.48,.345,.32));
    chest=smoothIntersection(chest,(2.46+.24*abs(p.x)-p.y)/1.03,.026);
    float neckHole=ell(p-vec3(0,3.135,.055),vec3(.235,.16,.30));
    chest=smoothIntersection(chest,-neckHole,.018);
    float bibWidth=.14+.82*clamp(p.y-2.49,0.,.24);
    float bibTop=2.965-.082*exp(-pow(p.x/.18,2.));
    float yoke=max(abs(p.x)-bibWidth,p.y-bibTop);
    yoke=min(yoke,max(.30-abs(p.x),abs(p.y-2.96)-.049));
    add(h,chest,(yoke<0. && p.z>.13)?IVORY:LILAC);
    q=p-vec3(0,2.385,.268);
    float abdomen=fiveSides(q.xy,vec2(-.125,.095),vec2(.125,.095),
                            vec2(.155,-.025),vec2(0,-.095),vec2(-.155,-.025));
    add(h,roundedExtrusion(abdomen,abs(q.z)-.04,.020),IVORY);
    q=p-vec3(0,2.18,0);
    add(h,smoothIntersection(ell(q,vec3(.385,.13,.265)),abs(q.y)-.06,.025),GOLD);
    q=p-vec3(0,2.21,.275);
    float buckle=fiveSides(q.xy,vec2(-.11,.085),vec2(.11,.085),
                           vec2(.15,-.02),vec2(0,-.09),vec2(-.15,-.02));
    add(h,roundedExtrusion(buckle,abs(q.z)-.045,.015),GOLD);
    add(h,ell(p-vec3(0,2.04,0),vec3(.39,.285,.295)),JOINT);

    // Bowed flight shells leave a deliberate reveal around the central spine.
    add(h,box(p-vec3(0,2.69,-.395),vec3(.066,.355,.042),.024),JOINT);
    for(int i=0;i<5;i++)
        add(h,box(p-vec3(0,2.405+.135*float(i),-.462),vec3(.054,.029,.023),.010),JOINT);
    s=p; s.x=abs(s.x);
    add(h,cap(s,vec3(.06,3.015,-.395),vec3(.25,3.085,-.435),.034),JOINT);
    add(h,cylZ(s-vec3(.165,3.05,-.465),.045,.016)-.006,STEEL);
    add(h,cylZ(s-vec3(.165,3.05,-.488),.022,.004),JOINT);
    q=p-vec3(0,2.12,-.215);
    add(h,smoothIntersection(ell(q,vec3(.165,.19,.075)),-q.y-.17+.6*abs(q.x),.02),OCHRE);
    q=podCoordinates(p);
    d=podShell(q);
    s=p; s.x=abs(s.x);
    vec3 nozzle=nozzleCoordinates(s);
    float bore=cylZ((nozzle-vec3(0,.075,0)).xzy,.090,.15);
    d=max(d,-bore);
    add(h,d,LILAC);
    float band=podBand(q);
    add(h,max(d-.0025,abs(band-.12)-.052),IVORY);
    add(h,max(d-.0025,abs(band+.305)-.072),IVORY);
    // Dark recessed throat, metal lip and a luminous injector deep inside the opening.
    float housing=ell(nozzle-vec3(0,.033,0),vec3(.119,.077,.121));
    add(h,max(housing,-cylZ(nozzle.xzy,.087,.20)),JOINT);
    float rim=length(vec2(length(nozzle.xz)-.098,nozzle.y+.015))-.007;
    add(h,rim,STEEL);
    add(h,cylZ((nozzle-vec3(0,.125,0)).xzy,.089,.012),JOINT);
    add(h,ell(nozzle-vec3(0,.106,0),vec3(.070,.009,.070)),CYAN);

    // A slim, hollow neck band leaves the throat visible above the chest opening.
    q=p-vec3(0,3.183,.158);
    float collarSide=(length(q.xz/vec2(.147,.116))-1.)*.116;
    float collarHole=(length(q.xz/vec2(.111,.090))-1.)*.090;
    float collar=roundedExtrusion(collarSide,abs(q.y)-.031,.008);
    add(h,max(collar,-collarHole),GOLD);
    add(h,cylZ(p-vec3(0,3.183,.283),.021,.005)-.002,JOINT);

    return h;
}

vec2 scene(vec3 p) {
    vec2 h=vec2(p.y,0.);
    float bound=box(p-vec3(0,2.15,0),vec3(1.55,2.3,2.4),.03);
    if(bound>.35) { add(h,bound,-1.); return h; }
    vec2 part=torsoScene(localPosition(p,0)); add(h,part.x,part.y);
    part=headScene(localPosition(p,1)); add(h,part.x*.88,part.y+20.);
    for(int side=0;side<2;side++) {
        int leg=2+2*side,arm=6+2*side,shoulder=10+side;
        part=upperLegScene(localPosition(p,leg)); add(h,part.x,part.y+20.*float(leg));
        part=lowerLegScene(localPosition(p,leg+1)); add(h,part.x,part.y+20.*float(leg+1));
        part=upperArmScene(localPosition(p,arm)); add(h,part.x,part.y+20.*float(arm));
        part=forearmScene(localPosition(p,arm+1)); add(h,part.x,part.y+20.*float(arm+1));
        part=shoulderScene(localPosition(p,shoulder)); add(h,part.x,part.y+20.*float(shoulder));
    }
    return h;
}
vec4 surfaceGradient(vec3 p) {
    vec3 n=vec3(0);
    for(int i=0;i<4;i++) {
        vec3 e=.5773503*(2.*vec3(float((i+3)/2%2),float(i/2%2),float(i%2))-1.);
        n+=e*scene(p+e*.0015).x;
    }
    // Preserve the gradient magnitude: conservative distance estimates are not unit SDFs.
    float magnitude=length(n)/.002;
    return vec4(normalize(n),clamp(magnitude,.12,1.5));
}
vec2 secondaryScene(vec3 p) {
    // Secondary rays resolve the larger forms; eyelids and grooves use local shading.
    vec2 h=vec2(p.y,0.),piece;
    vec3 q=localPosition(p,1);
    vec2 hood=helmetShell(q); add(h,hood.x*.88,hood.y);
    add(h,skinGeometry(q)*.88,SKIN);
    float braid=min(cap(q,braidCenter(0.),braidCenter(.40),.095),
                    cap(q,braidCenter(.40),braidTip,.078));
    add(h,braid*.88,HAIR);
    piece=torsoScene(localPosition(p,0)); add(h,piece.x,piece.y);
    for(int side=0;side<2;side++) {
        int leg=2+side*2,arm=6+side*2,shoulder=10+side;
        piece=upperLegScene(localPosition(p,leg)); add(h,piece.x,piece.y);
        piece=lowerLegScene(localPosition(p,leg+1)); add(h,piece.x,piece.y);
        piece=upperArmScene(localPosition(p,arm)); add(h,piece.x,piece.y);
        piece=forearmScene(localPosition(p,arm+1)); add(h,piece.x,piece.y);
        piece=shoulderScene(localPosition(p,shoulder)); add(h,piece.x,piece.y);
    }
    return h;
}
float shadow(vec3 p,vec3 l,float distanceScale) {
    float v=1.,t=.025;
    for(int i=0;i<SHADOW_STEPS;i++) {
        vec2 hit=secondaryScene(p+l*t);
        float d=hit.x;
        // Bounds accelerate traversal but do not represent shadow-casting surfaces.
        if(mod(hit.y,20.)<18.) v=min(v,7.*d/(t*distanceScale));
        // Small near-surface steps keep grazing rays from skipping narrow plate details.
        t+=clamp(d*.9,.004,.10);
        if(d<.0008 || t>6.) break;
    }
    return clamp(v,0.,1.);
}
vec3 materialAlbedo(float m);
float ambientOcclusion(vec3 p,vec3 n,float distanceScale,out vec3 bleed) {
    float v=0.,w=1.;
    bleed=vec3(0);
    for(int i=1;i<=4;i++) {
        float t=.055*float(i);
        vec2 hit=secondaryScene(p+n*t);
        if(mod(hit.y,20.)<18.) {
            float separation=hit.x/distanceScale;
            v+=max(t-separation,0.)*w;
            // Reuse these probes for restrained neighbor-color bounce, not full GI.
            float proximity=clamp(1.-separation/t,0.,1.);
            bleed+=materialAlbedo(mod(hit.y,20.))*proximity*w;
        }
        w*=.55;
    }
    bleed*=.16;
    return clamp(1.-2.7*v,.25,1.);
}
struct Surface {
    vec3 base;
    float roughness;
    float metal;
    float coat;
    float specular;
};
Surface material(float m) {
    // Linear-light colors; colored paint is a dielectric, not bare metal.
    if(m>12.5) return Surface(vec3(.065,.014,.004),.6,0.,0.,.02);
    if(m>11.5) return Surface(vec3(.28,.112,.062),.57,0.,0.,.018);
    if(m>10.5) return Surface(vec3(.36,.20,.035),.65,0.,0.,.025);
    if(m>8.5) return Surface(vec3(.20,.23,.26),.32,.78,0.,.04);
    if(m<.5) return Surface(vec3(.38,.35,.32),.90,0.,0.,.025);
    if(m<1.5) return Surface(vec3(.34,.15,.54),.38,0.,.20,.04);
    if(m<2.5) return Surface(vec3(.86,.67,.45),.43,0.,.13,.04);
    if(m<3.5) return Surface(vec3(.025,.028,.035),.65,0.,0.,.03);
    if(m<4.5) return Surface(vec3(.33,.134,.047),.59,0.,0.,.022);
    if(m<5.5) return Surface(vec3(.012,.009,.007),.52,0.,0.,.025);
    if(m<6.5) return Surface(vec3(.64,.365,.095),.34,.72,.08,.04);
    if(m<7.5) return Surface(vec3(.012,.22,.29),.16,.18,.5,.04);
    return Surface(vec3(.78,.73,.61),.16,0.,.08,.028);
}
vec3 materialAlbedo(float m) { return material(m).base; }
vec3 skinTransmission(vec3 p,vec3 n,vec3 light) {
    // Three interior probes estimate optical thickness of the sculpted skin only.
    // These distance bounds are not exact path lengths or a physical SSS solution.
    vec3 transmission=vec3(0);
    for(int i=0;i<3;i++) {
        float depth=i==0?.035:(i==1?.085:.17);
        float weight=i==0?.50:(i==1?.32:.18);
        vec3 probe=p-normalize(n*.6+light*.4)*depth;
        float inside=skinGeometry(probe)/.70;
        float thickness=max(.006,depth-max(-inside,0.));
        transmission+=weight*exp(-thickness*vec3(12,28,48));
    }
    return transmission;
}
void eyeSurface(vec3 p,vec3 localView,float pixel,inout Surface surf,out float occlusion) {
    vec3 q=eyeCoordinates(p);
    localView.x*=sign(p.x);
    localView.xz=rot(-.21)*localView.xz;
    localView.xy=rot(.055)*localView.xy;
    vec3 corneaNormal=normalize(q/(EYE_RADII*EYE_RADII));
    vec3 ray=refract(-normalize(localView),corneaNormal,1./1.336);
    // Intersect the refracted ray with a recessed iris plane under the cornea.
    float t=(.059-q.z)/min(ray.z,-.05);
    vec2 iris=((q+max(t,0.)*ray).xy-vec2(-.006,.011))*vec2(1.15,.85);
    float radius=length(iris),a=atan(iris.y,iris.x);
    float aa=max(pixel*1.2,.0006);
    float irisMask=1.-smoothstep(.073-aa,.073+aa,radius);
    float pupil=1.-smoothstep(.044-aa,.044+aa,radius);
    float limbus=smoothstep(.060,.072,radius);
    float fibers=.95+.03*sin(a*43.+radius*180.)+.02*sin(a*79.-radius*240.);
    float ring=exp(-pow((radius-.045)/.014,2.));
    vec3 brown=mix(vec3(.12,.035,.006),vec3(.34,.145,.024),ring*.85)*fibers;
    brown*=.36+.64*smoothstep(-.015,.065,-iris.y);
    brown*=1.-limbus*.72;
    brown=mix(brown,vec3(.0025,.0018,.0014),pupil);
    surf.base=mix(surf.base,brown,irisMask);
    float upper=eyelidHeights(q.x).x;
    occlusion=.32+.68*smoothstep(.006,.110,upper-q.y);
    surf.base*=occlusion;
}

// Analytic panel lines and fastener recesses, evaluated only at a surface hit.
// These are shallow shading details, so the ray-marched silhouette stays intact.
vec2 armorDetail(vec3 p,float m) {
    vec3 q=p; q.x=abs(q.x);
    float seam=1.,rivet=1.;
    if(p.y>3.28) {
        q=p-vec3(0,3.77,-.035);
        if(m==IVORY) {
            float a=atan(q.x,q.y);
            seam=abs(sin(4.*a+.2))*.14;
            // A small pair of cheek fasteners, set into the ivory rim.
            rivet=length(vec2(abs(q.x)-.43,q.y+.39));
        } else {
            float a=atan(q.x,q.z);
            seam=min(abs(abs(a)-.67),abs(abs(a)-1.9))*.48;
            seam=min(seam,abs(q.y-.39));
            rivet=length(vec2(abs(q.x)-.40,q.y-.36));
        }
        // Side discs have their own concentric machining line.
        if(abs(p.x)>.679 && length(q.yz)<.215) {
            seam=abs(length(q.yz)-.139); rivet=1.;
        }
        return vec2(seam,rivet);
    }
    if(q.x>.72 && p.y<2.56 && p.y>1.70) {
        q=gauntletCoordinates(p);
        if(m==IVORY) {
            seam=min(abs(q.y-.13-.28*q.z),abs(q.y+.16+.30*q.z));
            seam=min(seam,abs(q.z-.10+.25*q.y));
            rivet=length(vec2(q.y-.255,q.z+.045));
        } else {
            seam=min(abs(q.y-.265),abs(q.y+.25));
            seam=min(seam,abs(q.x+.18));
            rivet=length(vec2(q.x+.11,q.y-.20));
        }
        return vec2(seam,rivet);
    } else if(p.y<1.41 && p.y>.40) {
        q-=vec3(.4,0,.025);
        q.z-=.035*sin(PI*clamp((1.41-q.y)/1.12,0.,1.));
        vec2 radii=shinRadii(q.y);
        float angle=atan(q.x/radii.x,q.z/radii.y);
        seam=min(abs(abs(angle)-.66),abs(abs(angle)-2.40))*.23;
        seam=min(seam,abs(q.y-1.275));
        seam=min(seam,abs(ankleOpening(q.xy)+.098));
        rivet=length(vec2(abs(q.x)-.14,q.y-1.10));
        return vec2(seam,rivet);
    } else if(p.y<.40) {
        q-=vec3(.4,0,0);
        float toe=q.z-(.46-.12*pow(q.x/.33,2.));
        seam=min(abs(toe)*.65,abs(footOutline(q)+.032));
        rivet=length(vec2(abs(q.x)-.20,q.z-.38));
        return vec2(seam,rivet);
    } else if(p.y>2.54 && q.x>.47 && p.z>-.32) {
        if(p.z>.355 && length(p.xy-vec2(sign(p.x)*.6,2.98))<.17)
            return vec2(abs(length(p.xy-vec2(sign(p.x)*.6,2.98))-.135),1.);
        q-=vec3(.62,3.00,.015);
        seam=abs(shoulderProfile(q.xy)+.032);
        rivet=length(q.xy-vec2(.315,-.20));
        return vec2(seam,rivet);
    } else if(p.z<-.38 && p.y>2.15) {
        q=podCoordinates(p);
        float angle=atan(q.x/.27,q.z/.18);
        seam=abs(abs(angle)-2.48)*.20;
        seam=min(seam,abs(podBand(q)+.205));
        // A small inset service hatch sits on the broad lower ivory panel.
        vec2 hatch=q.xy-vec2(.075,-.29);
        float hatchEdge=box(vec3(hatch,0),vec3(.045,.017,.1),.006);
        seam=min(seam,abs(hatchEdge));
        rivet=length(vec2(q.x+.105,q.y-.185));
        return vec2(seam,rivet);
    } else if(p.y>2.56 && q.x<.43) {
        // A shallow contour follows the upper lip of the ivory chest inset.
        seam=abs(p.y-(2.965-.082*exp(-pow(p.x/.18,2.))));
        if(p.y<2.91) seam=min(seam,abs(p.x));
        rivet=length(vec2(abs(p.x)-.275,p.y-2.915));
        return vec2(seam,rivet);
    } else return vec2(1.);
}
float armorHeight(vec2 d) {
    float groove=exp(-pow(d.x/.0027,2.));
    float recess=exp(-pow(d.y/.008,4.));
    return -.0015*groove-.002*recess;
}
vec2 detailAt(vec3 p,float m,int part) { return armorDetail(localPosition(p,part),m); }
float hash31(vec3 p) {
    p=fract(p*.1031); p+=dot(p,p.yzx+33.33);
    return fract((p.x+p.y)*p.z);
}
float noise3(vec3 p) {
    vec3 a=floor(p),f=fract(p); f=f*f*(3.-2.*f);
    return mix(mix(mix(hash31(a),hash31(a+vec3(1,0,0)),f.x),
                   mix(hash31(a+vec3(0,1,0)),hash31(a+vec3(1,1,0)),f.x),f.y),
               mix(mix(hash31(a+vec3(0,0,1)),hash31(a+vec3(1,0,1)),f.x),
                   mix(hash31(a+vec3(0,1,1)),hash31(a+vec3(1,1,1)),f.x),f.y),f.z);
}
float scratches(vec2 uv,float pixel,float seed) {
    vec2 cell=floor(uv*7.),q=fract(uv*7.);
    float h=hash31(vec3(cell,seed));
    vec2 center=.25+.5*vec2(hash31(vec3(cell,seed+2.)),hash31(vec3(cell,seed+7.)));
    q=rot(-.65+1.2*h)*(q-center)/7.;
    float halfLength=.009+.032*hash31(vec3(cell,seed+13.));
    float d=length(vec2(max(abs(q.x)-halfLength,0.),q.y));
    float width=.0006+.0008*h,aa=max(pixel,.0005);
    return (1.-smoothstep(width,width+aa,d))*smoothstep(.62,.77,h)*min(1.,width/aa);
}
void armorWear(vec3 p,vec3 n,vec2 detail,float pixel,int part,inout Surface surf) {
    if(WEAR==0) return;
    // Authored contact zones keep wear off broad, protected paint surfaces.
    float edge=detail.x+.020,contact=.05,dust=0.;
    vec3 q=p; q.x=abs(q.x);
    if(part==3 || part==5) {
        q-=vec3(.4,0,0);
        if(q.y<.40) {
            edge=min(abs(q.y-.135),abs(q.z+.45*q.y-.69)*.8);
            contact=.40+.6*smoothstep(.33,.65,q.z);
            dust=(1.-smoothstep(.07,.24,q.y))*.09;
        } else if(q.y<1.42) {
            vec3 shin=q;
            shin.z-=.025+.035*sin(PI*clamp((1.41-q.y)/1.12,0.,1.));
            float side=curvedSection(shin.xz,shinRadii(q.y));
            edge=max(min(abs(ankleOpening(q.xy)),abs(q.y-1.41+.10*q.z)),abs(side));
            contact=.24;
        } else { edge=min(edge,.7*abs(q.y-1.58)); contact=.45; }
    } else if(part==7 || part==9) {
        q=gauntletCoordinates(p);
        float side=curvedSection(q.xz,vec2(.177,.185)+.055*smoothstep(-.32,.22,q.y));
        float end=min(abs(q.y+.32-.12*q.x),abs(q.y-.31+.28*q.x));
        edge=min(detail.x+.010,max(end,abs(side)));
        contact=.34;
    } else if(part>=10) {
        q-=vec3(.62,3.,.015);
        float radius=.31*(1.-.5*smoothstep(.05,.45,-q.y));
        float side=curvedSection(vec2(q.x+.02,q.z),vec2(.5,radius));
        edge=min(edge,max(abs(shoulderProfile(q.xy)),abs(side)));
        contact=.14;
    } else if(part==0 && p.z<-.38 && p.y>2.12) {
        q=podCoordinates(p);
        float side=ell(q-vec3(0,.01,0),vec3(.25+.035*clamp(.5-q.y,0.,1.),.60,.18));
        edge=min(max(abs(q.y+.445+.65*q.x-.20*q.z),abs(side)),
                 min(abs(abs(podBand(q)-.12)-.052),abs(abs(podBand(q)+.305)-.072)));
        contact=.19;
    } else if(part==1) {
        q=p-vec3(0,3.77,-.035);
        float opening=(length((q.xy-vec2(0,-.045))/vec2(.505,.548))-1.)*.505;
        if(q.z>.075) edge=min(edge,abs(opening-.090));
        contact=.035;
    }
    // Three-dimensional masks stay attached through all joint and camera motion.
    vec3 sampleP=p+vec3(float(part)*1.71,0,.37*float(part));
    float coarse=noise3(sampleP*32.),fine=noise3(sampleP*135.);
    float aa=max(pixel,.0007);
    float reach=.002+.014*smoothstep(.54,.76,coarse);
    float chips=(1.-smoothstep(reach-aa,reach+aa,edge))*smoothstep(.50,.72,fine);
    chips*=1.-smoothstep(.005,.016,pixel);
    vec3 weights=pow(abs(n),vec3(6.)); weights/=max(dot(weights,vec3(1)),.001);
    float scratch=dot(weights,vec3(scratches(p.zy,pixel,float(part)+1.),
                                   scratches(p.xz,pixel,float(part)+8.),
                                   scratches(p.xy,pixel,float(part)+19.)));
    scratch*=.16+.65*contact+.45*exp(-edge/.04);
    vec3 primer=mix(vec3(.12,.105,.15),vec3(.23,.21,.19),surf.base.r);
    surf.base=mix(surf.base,primer,chips*.85);
    float metal=chips*smoothstep(.65,.83,fine);
    surf.base=mix(surf.base,vec3(.32,.33,.35),metal);
    surf.base=mix(surf.base,surf.base*.60+vec3(.16,.145,.12),scratch*.8);
    surf.base=mix(surf.base,vec3(.25,.205,.16),dust*(.55+.45*coarse));
    surf.roughness=clamp(surf.roughness+.035*(noise3(sampleP*9.)-.5)
                        +.12*scratch+.10*chips+.06*contact*coarse,.20,.75);
    surf.metal=max(surf.metal,metal*.7);
    surf.coat*=1.-clamp(chips+scratch*.5+dust,0.,1.);
}
void finishArmor(vec3 p,vec3 geometricNormal,float m,float pixel,int part,
                 inout vec3 n,inout Surface surf) {
    vec2 d=detailAt(p,m,part);
    float aa=max(pixel*.65,.00065);
    float line=1.-smoothstep(.0015,.0015+aa,d.x);
    float lip=1.-smoothstep(.0015,.0015+aa,abs(d.x-.005));
    float recess=1.-smoothstep(.005,.008+aa,d.y);
    surf.base*=1.-.38*line-.17*recess;
    surf.base=mix(surf.base,surf.base*1.12,lip*.6);
    surf.roughness+=line*.09+recess*.13;
    // Finite differences of the shallow height field give a recessed normal.
    float e=max(pixel*.6,.0008),a=armorHeight(d);
    vec3 g=vec3(armorHeight(detailAt(p+vec3(e,0,0),m,part))-a,
                armorHeight(detailAt(p+vec3(0,e,0),m,part))-a,
                armorHeight(detailAt(p+vec3(0,0,e),m,part))-a)/e;
    g-=geometricNormal*dot(g,geometricNormal);
    g*=min(1.,.3/max(length(g),.0001));
    n=normalize(geometricNormal-g*.65);
    armorWear(localPosition(p,part),normalize(partFrame[part]*geometricNormal),d,pixel,part,surf);
}

vec3 fresnel(float vh,vec3 f0) {
    return f0+(1.-f0)*pow(clamp(1.-vh,0.,1.),5.);
}
float distribution(float nh,float roughness) {
    float a=roughness*roughness,a2=a*a;
    float d=nh*nh*(a2-1.)+1.;
    return a2/max(PI*d*d,.00001);
}
vec3 directLight(Surface s,vec3 n,vec3 v,vec3 l,vec3 radiance,float visibility,float skin) {
    float nl=max(dot(n,l),0.),nv=max(dot(n,v),.001);
    vec3 h=normalize(v+l);
    float nh=max(dot(n,h),0.),vh=max(dot(v,h),0.);
    vec3 f0=mix(vec3(s.specular),s.base,s.metal),f=fresnel(vh,f0);
    // A finite light size broadens highlights rather than producing pinpricks.
    float rough=sqrt(s.roughness*s.roughness+.018);
    float k=pow(rough+1.,2.)/8.;
    float gv=nv/(nv*(1.-k)+k),gl=nl/(nl*(1.-k)+k);
    vec3 spec=distribution(nh,rough)*gv*gl*f/max(4.*nl*nv,.001);
    float diffuse=mix(nl,max((dot(n,l)+.30)/1.30,0.),skin);
    vec3 result=(1.-f)*(1.-s.metal)*s.base*(diffuse/PI);
    result+=spec*nl;
    float coat=distribution(nh,.25)*gv*gl/max(4.*nv,.001);
    result+=s.coat*.04*coat;
    return result*radiance*visibility;
}
float softbox(vec3 r,vec3 direction,vec2 size,float blur) {
    vec3 right=normalize(cross(direction,vec3(0,1,0))),up=cross(right,direction);
    float facing=dot(r,direction);
    vec2 uv=vec2(dot(r,right),dot(r,up))/max(facing,.001);
    vec2 mask=1.-smoothstep(size-vec2(blur),size+vec2(blur),abs(uv));
    return mask.x*mask.y*smoothstep(0.,.15,facing);
}
vec3 studioReflection(vec3 r,float roughness) {
    vec3 env=mix(vec3(.095,.080,.072),vec3(.28,.27,.29),smoothstep(-.35,.8,r.y));
    float blur=.035+roughness*roughness*.85;
    env+=vec3(3.3,3.1,2.9)*softbox(r,normalize(vec3(-3,5,4)),vec2(.32,.48),blur);
    env+=vec3(.95,1.03,1.18)*softbox(r,normalize(vec3(4,2,3)),vec2(.19,.52),blur);
    env+=vec3(1.7,1.45,1.8)*softbox(r,normalize(vec3(1,3,-4)),vec2(.18,.42),blur);
    return env;
}
vec3 hairTangent(vec3 p) {
    if(p.x<-.32 && p.y<3.55) {
        float t=clamp(RUN_POSE==1?(-.365-p.x)/.855:(3.565-p.y)/1.045,0.,1.);
        return normalize(braidCenter(t+.005)-braidCenter(t-.005));
    }
    return normalize(vec3(.85,sign(p.x)*-.6,.15));
}
vec2 jetInterval(vec3 ro,vec3 rd) {
    // Intersect only a tight local volume; most screen pixels do no plume work.
    vec3 inv=1./(sign(rd+vec3(1e-8))*max(abs(rd),vec3(1e-7)));
    vec3 a=(vec3(-.19,-.64,-.19)-ro)*inv;
    vec3 b=(vec3(.19,.12,.19)-ro)*inv;
    vec3 lo=min(a,b),hi=max(a,b);
    return vec2(max(lo.x,max(lo.y,lo.z)),min(hi.x,min(hi.y,hi.z)));
}
vec4 integrateJet(vec3 ro,vec3 rd,vec2 interval,float solidDepth,float seed,float sampleOffset) {
    float begin=max(interval.x,0.),end=min(interval.y,solidDepth);
    if(end<=begin) return vec4(0,0,0,1);
    float stepLength=(end-begin)/32.;
    vec3 radiance=vec3(0); float transmission=1.;
    float time=iTime+seed;
    for(int i=0;i<32;i++) {
        vec3 p=ro+rd*(begin+(float(i)+.5+sampleOffset*.65)*stepLength);
        float axial=max(-p.y,0.);
        float tail=1.-smoothstep(.24,.61,axial);
        float ignition=1.-smoothstep(.055,.115,p.y);
        // Advected noise deforms the gas, with increasing breakup downstream.
        vec3 flow=vec3(p.x*39.,axial*15.-time*9.,p.z*39.+seed*7.);
        float turbulence=noise3(flow),fine=noise3(flow*1.93+vec3(7,3,-5));
        vec2 center=.012*axial*vec2(sin(axial*16.-time*8.),cos(axial*13.-time*11.));
        float width=.059*(1.-.52*clamp(axial/.61,0.,1.));
        width*=1.+.11*sin(axial*34.-.6*sin(time*4.));
        float radius=length(p.xz-center)/width;
        radius+=(turbulence-.5)*(.22+1.7*axial);
        float core=exp(-3.4*radius*radius)*tail*ignition;
        float sheath=exp(-1.05*radius*radius)*tail*ignition;
        sheath*=mix(.60,1.35,turbulence)*mix(.75,1.2,fine);
        // Pressure cells sit in a narrow fast core; the surrounding gas remains soft.
        float cells=pow(.5+.5*cos(axial*37.-.45*sin(time*3.)),7.);
        cells*=exp(-7.*radius*radius)*exp(-2.8*axial)*tail*ignition;
        float pulse=.94+.06*sin(time*17.);
        vec3 emission=(vec3(.008,.46,1.25)*sheath+vec3(.24,2.3,3.8)*core
                       +vec3(2.8,3.4,3.8)*cells)*pulse*16.;
        float extinction=(.45*sheath+core)*8.;
        float segment=exp(-extinction*stepLength);
        radiance+=transmission*emission*(1.-segment)/max(extinction,.0001);
        transmission*=segment;
    }
    return vec4(radiance,transmission);
}
vec3 compositeJets(vec3 col,vec3 ro,vec3 rd,float solidDepth,vec2 sampleOffset) {
    if(JETS==0) return col;
    ro=localPosition(ro,0); rd=partFrame[0]*rd;
    vec3 a=nozzleCoordinates(ro),ad=nozzleCoordinates(ro+rd)-a;
    ro.x=-ro.x; rd.x=-rd.x;
    vec3 b=nozzleCoordinates(ro),bd=nozzleCoordinates(ro+rd)-b;
    vec2 ia=jetInterval(a,ad),ib=jetInterval(b,bd);
    vec4 left=integrateJet(a,ad,ia,solidDepth,0.,sampleOffset.x+sampleOffset.y*.5);
    vec4 right=integrateJet(b,bd,ib,solidDepth,1.73,sampleOffset.x+sampleOffset.y*.5);
    // Disjoint plumes are composited in camera order, in linear light.
    if(ia.x<ib.x) { col=right.rgb+right.a*col; col=left.rgb+left.a*col; }
    else { col=left.rgb+left.a*col; col=right.rgb+right.a*col; }
    return col;
}
vec3 render(vec2 uv,vec2 lightSample) {
    vec3 ro,ww,uu,vv,rd;
#ifdef PUCK_STUDY
    // Puck's paired authored camera (iCameraFov is 0 unless the views.studies row names one) replaces the iMouse
    // orbit below, so the study renders from the same eye the SDF pane's own camera resolved to. This study's
    // units are twice the world's (face centre y=3.725 here, y=1.875 in moth.world.json's creation; both stand on
    // y=0) and this moth faces +Z where the world's faces -Z, so a world point maps by worldToStudy: a 180-degree
    // yaw (x and z negated, never a mirror) then the scale. uv.y spans [-.5,.5] (see mainImage), so the focal
    // length for a vertical field of view f is .5/tan(f/2).
    const vec3 worldToStudy=vec3(-2.,2.,-2.);
    if(iCameraFov>0.) {
        vec3 target=iCameraTarget*worldToStudy;
        ro=iCameraPos*worldToStudy;
        ww=normalize(target-ro); uu=normalize(cross(ww,normalize(iCameraUp*worldToStudy)));
        vv=cross(uu,ww);
        rd=normalize(uv.x*uu+uv.y*vv+(.5/tan(iCameraFov*.5))*ww);
    } else
#endif
    {
    float yaw=RUN_POSE==1?-.75:.22, pitch=.055;
    if(PACK_VIEW==1) { yaw=-2.65; pitch=.10; }
    if(AUTO_TURN==1) yaw+=iTime*.30;
    if(abs(iMouse.z)>1.) {
        yaw=(iMouse.x/iResolution.x-.5)*2.*PI;
        pitch=clamp((iMouse.y/iResolution.y-.5)*1.3,-.32,.72);
    }
    vec3 target=vec3(0,2.22,0);
    float distance=11.8;
    if(CLOSE_UP==1) { target=vec3(0,3.54,.06); distance=4.8; }
    if(PACK_VIEW==1) { target=vec3(0,2.63,-.32); distance=6.8; }
    ro=target+distance*vec3(sin(yaw)*cos(pitch),sin(pitch),cos(yaw)*cos(pitch));
    ww=normalize(target-ro); uu=normalize(cross(ww,vec3(0,1,0)));
    vv=cross(uu,ww);
    rd=normalize(uv.x*uu+uv.y*vv+2.25*ww);
    }
    vec3 col=mix(vec3(.49,.455,.415),vec3(.34,.32,.31),smoothstep(-.5,.8,uv.y));
    vec3 background=col;
    float t=0.; vec2 h=vec2(1,0); bool hit=false;
    for(int i=0;i<MAX_STEPS;i++) {
        vec3 p=ro+rd*t; h=scene(p);
        if(h.x<.0009*max(1.,t*.16)) { hit=true; break; }
        t+=max(h.x*.78,.00035);
        if(t>80.) break;
    }
    if(hit) {
        vec3 p=ro+rd*t;
        vec4 gradient=surfaceGradient(p);
        vec3 ng=gradient.xyz,n=ng,v=-rd;
        int part=int(floor(h.y/20.));
        vec3 paintP=localPosition(p,part);
        h.y-=20.*float(part);
        Surface surf=material(h.y);
        float pixel=t/(iResolution.y*2.25);
        if(h.y==LILAC || h.y==IVORY) finishArmor(p,ng,h.y,pixel,part,n,surf);
        float skin=(h.y==SKIN || h.y==LIP)?1.:0.;
        if(skin>.5) {
            vec2 cheekP=(vec2(abs(paintP.x),paintP.y)-vec2(.255,3.595))/vec2(.11,.072);
            float cheek=exp(-dot(cheekP,cheekP));
            surf.base=mix(surf.base,vec3(.39,.105,.047),cheek*.25);
            float mouthX=clamp(paintP.x,-.133,.133);
            vec2 lipP=vec2(paintP.x/.115,(paintP.y-smileHeight(mouthX)+.014)/.012);
            float lip=exp(-dot(lipP,lipP))*smoothstep(.36,.47,paintP.z);
            surf.base=mix(surf.base,vec3(.43,.158,.069),lip*.55);
            // Keep broad facial lighting smooth while preserving the modeled nose and lips.
            vec3 guide=normalize((paintP-FACE_CENTER)/(FACE_RADII*FACE_RADII));
            guide=normalize(transpose(partFrame[part])*guide);
            float nose=exp(-dot((paintP.xy-vec2(0,3.62))/vec2(.085,.17),(paintP.xy-vec2(0,3.62))/vec2(.085,.17)));
            float front=smoothstep(.36,.50,paintP.z);
            if(h.y==SKIN) n=normalize(mix(n,guide,.10*front*(1.-nose)*(1.-lip)));
        }
        float eyeOcclusion=1.;
        if(h.y==EYE) {
            eyeSurface(paintP,partFrame[part]*v,pixel,surf,eyeOcclusion);
            // The cornea has its own optical normal; socket CSG must not flatten it.
            vec3 cornea=normalize(eyeCoordinates(paintP)/(EYE_RADII*EYE_RADII));
            cornea.xy=rot(-.055)*cornea.xy;
            cornea.xz=rot(.21)*cornea.xz;
            cornea.x*=sign(paintP.x);
            n=normalize(transpose(partFrame[part])*cornea);
        }
        vec3 bleed;
        float ao=ambientOcclusion(p,ng,gradient.w,bleed);
        if(skin>.5) ao=mix(ao,1.,.35);
        if(h.y==EYE) ao=max(ao,.70);
        vec3 key=normalize(vec3(-3.,5.,4.));
        vec3 fill=normalize(vec3(4.,2.,3.));
        vec3 rim=normalize(vec3(1.,3.,-4.));
        vec3 lightRight=normalize(cross(key,vec3(0,1,0)));
        vec3 lightUp=cross(lightRight,key);
        vec3 shadowDirection=normalize(key+.42*(lightSample.x*lightRight+lightSample.y*lightUp));
        float sh=shadow(p+ng*.008,shadowDirection,gradient.w);
        col=directLight(surf,n,v,key,vec3(3.15,3.,2.85),sh,skin);
        col+=directLight(surf,n,v,fill,vec3(.65,.70,.82),ao,skin);
        col+=directLight(surf,n,v,rim,vec3(1.7,1.45,1.9),ao,0.);
        vec3 ambient=mix(vec3(.15,.105,.08),vec3(.26,.25,.29),n.y*.5+.5);
        col+=surf.base*(1.-surf.metal)*ambient*ao;
        col+=surf.base*(1.-surf.metal)*bleed;
        if(skin>.5) {
            vec3 localN=normalize(partFrame[part]*ng);
            vec3 localLight=normalize(partFrame[part]*rim);
            vec3 transmission=skinTransmission(paintP,localN,localLight);
            float backLight=pow(max(dot(-n,rim),0.),1.5);
            col+=surf.base*transmission*vec3(.9,.35,.13)*backLight*.55;
        }
        if(JETS==1 && h.y!=SKIN && h.y!=EYE && h.y!=HAIR) {
            // A restrained, art-directed cyan bounce on aft-facing nearby hardware.
            vec3 rear=localPosition(p,0),rearNormal=normalize(partFrame[0]*n);
            vec3 source=vec3(sign(rear.x)*.385,2.12,-.637)-rear;
            float reach=length(source);
            float bounce=max(dot(rearNormal,source/max(reach,.001)),0.);
            bounce*=exp(-reach*4.)/(.10+reach*reach)*(1.-smoothstep(-.34,-.20,rear.z));
            col+=surf.base*vec3(.015,.19,.28)*bounce*ao;
        }
        // Warm skin bounce is a deliberate illustration approximation, not SSS tracing.
        col+=skin*surf.base*vec3(.20,.085,.033)*(1.-max(dot(n,key),0.))*ao;
        vec3 f0=mix(vec3(surf.specular),surf.base,surf.metal);
        vec3 f=f0+(max(vec3(1.-surf.roughness),f0)-f0)*pow(1.-max(dot(n,v),0.),5.);
        float specAO=clamp(pow(ao,1.+surf.roughness),0.,1.);
        col+=studioReflection(reflect(-v,n),surf.roughness)*f*specAO*.75;
        if(h.y==EYE) {
            // A small camera-side portrait card keeps a readable corneal catchlight
            // through the turntable, alongside the fixed studio reflections.
            vec3 reflected=reflect(-v,n);
            vec3 portraitCard=normalize(v-.35*uu+.45*vv);
            float portraitReflection=softbox(reflected,portraitCard,vec2(.095,.115),.013);
            float keyReflection=softbox(reflected,key,vec2(.115,.15),.016);
            float fillReflection=softbox(reflected,fill,vec2(.035,.050),.012);
            col+=vec3(3.1,3.,2.8)*portraitReflection;
            col+=vec3(1.1,1.05,.96)*keyReflection*eyeOcclusion;
            col+=vec3(.35,.4,.5)*fillReflection;
        }
        if(h.y==HAIR && (abs(paintP.x)>.31 || paintP.y>3.94)) {
            vec3 tangent=normalize(transpose(partFrame[part])*hairTangent(paintP));
            vec3 halfVector=normalize(key+v);
            // Two shifted, bounded sheen lobes approximate surface and internal reflection.
            float th=dot(tangent,halfVector);
            float primary=pow(max(1.-pow(th+.05,2.),0.),65.);
            float secondary=pow(max(1.-pow(th-.10,2.),0.),18.);
            float strand=.90+.10*sin(dot(paintP,vec3(145.,53.,89.)));
            col+=(vec3(.044,.041,.038)*primary+vec3(.037,.025,.014)*secondary)
                  *strand*max(dot(n,key),0.)*sh;
        }
        if(h.y==CYAN) {
            // Dark lens edges and a pale luminous center retain the optic's volume.
            float facing=max(dot(n,v),0.);
            float core=pow(facing,7.);
            col+=vec3(.006,.27,.39)+vec3(.11,1.2,1.65)*pow(facing,3.);
            col+=vec3(.7,1.,1.)*core*.65;
        }
        if(h.y<.5) {
            // Broad contact grounding, supplementing ray-marched shadows.
            float contact=exp(-2.8*dot(p.xz,p.xz));
            col*=1.-.22*contact;
            col=mix(col,background,smoothstep(14.,45.,t));
        }
    }
    return compositeJets(col,ro,rd,hit?t:80.,lightSample);
}
void mainImage(out vec4 fragColor,in vec2 fragCoord) {
    float blink=exp(-pow((fract((iTime+1.1)/5.1)-.50)/.023,4.));
    eyeOpen=ANIMATE_FACE==1?1.-.98*blink:1.;
    preparePose();
    prepareHair();
    prepareBraid();
    vec3 color=vec3(0);
    for(int y=0;y<AA;y++) for(int x=0;x<AA;x++) {
        vec2 offset=(vec2(float(x),float(y))+.5)/float(AA)-.5;
        vec2 uv=(fragCoord+offset-.5*iResolution.xy)/iResolution.y;
        color+=render(uv,offset);
    }
    color/=float(AA*AA);
    // Tone mapping follows linear-light integration, including corneal highlights.
    color=(color*(2.51*color+.03))/(color*(2.43*color+.59)+.14);
    color=pow(max(color,0.),vec3(1./2.2));
    vec2 uv=(fragCoord-.5*iResolution.xy)/iResolution.y;
    color*=1.-.14*dot(uv,uv);
    fragColor=vec4(color,1.);
}
