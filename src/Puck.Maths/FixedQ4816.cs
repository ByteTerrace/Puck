using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;

namespace Puck.Maths;

/// <summary>
/// A signed binary fixed-point number in Q48.16 format: a 64-bit two's-complement value whose high 48 bits are the
/// integer part (including the sign) and whose low 16 bits are the fraction. The stored <see cref="Value"/> equals
/// the represented real number scaled by 2^16. The signed companion to <see cref="UFixedQ4816"/>. Every operation,
/// including the transcendentals, is deterministic: identical inputs produce identical bits on every machine.
/// </summary>
/// <param name="Value">The raw underlying storage — the represented real number scaled by <c>2¹⁶</c>.</param>
public readonly partial record struct FixedQ4816(long Value)
    : INumber<FixedQ4816>,
      ISignedNumber<FixedQ4816>,
      IMinMaxValue<FixedQ4816>,
      IPowerFunctions<FixedQ4816> {
    /// <summary>The number of fractional bits in the Q48.16 layout (<c>16</c>).</summary>
    public const int FractionBitCount = 16;
    /// <summary>The number of integer bits in the Q48.16 layout, including the sign bit (<c>48</c>).</summary>
    public const int IntegerBitCount = (TotalBitCount - FractionBitCount);
    /// <summary>The total number of bits in the underlying storage (<c>64</c>).</summary>
    public const int TotalBitCount = (8 * sizeof(long));

    private const ulong FractionBitMask = ((1UL << FractionBitCount) - 1UL);
    private const long IntegerBitMask = unchecked((long)~FractionBitMask);
    private const long MaxIntegerValue = (long.MaxValue >> FractionBitCount);
    private const long MinIntegerValue = (long.MinValue >> FractionBitCount);
    private const long RawEpsilon = 1L;
    private const ulong RawHalf = (1UL << (FractionBitCount - 1)); // the half-ULP threshold, in the fraction domain
    private const long RawOne = (1L << FractionBitCount);          // the raw representation of 1.0, in the value domain
    private const double RawOneInverse = (1d / RawOne);
    // The largest power-of-two-grid double strictly below 2^63 and the exactly-representable -2^63; clamping here
    // keeps (long) casts from wrapping.
    private const double ScaledMaximum = 9223372036854774784d;
    private const double ScaledMinimum = -9223372036854775808d;

    // Atan2 constants: half/full turns at Q61 for the octant fold-back.
    private const long Atan2HalfPiQ61 = 3622009729038561421L;  // round(π/2 · 2^61)
    private const long Atan2PiQ61 = 7244019458077122842L;      // round(π · 2^61)

    // SinCos constants. Reduction runs in turns (2^64 raw = one turn): the two's-complement wrap of the 128-bit
    // reduction product is the exact mod-2π. The kernels are odd/even Taylor polynomials at Q60 over |θ| ≤ π/2
    // (truncation ≤ 2⁻³⁰), Horner-evaluated on θ².
    private const int SinCosFractionBitCount = 60;
    private const long SinCosInvTwoPiQ64 = 2935890503282001226L;  // round(2^64 / 2π)
    private const long SinCosQuarterTurnQ64 = (1L << 62);
    private const long SinCosTwoPiQ60 = 7244019458077122842L;     // round(2π · 2^60)

    // sin θ = θ·Σ (−1)ᵏ θ²ᵏ/(2k+1)!, coefficients at Q60 for k = 0..6.
    private const long SinPolyC0Q60 = 1152921504606846976L;
    private const long SinPolyC1Q60 = -192153584101141163L;
    private const long SinPolyC2Q60 = 9607679205057058L;
    private const long SinPolyC3Q60 = -228754266787073L;
    private const long SinPolyC4Q60 = 3177142594265L;
    private const long SinPolyC5Q60 = -28883114493L;
    private const long SinPolyC6Q60 = 185148170L;

    // cos θ = Σ (−1)ᵏ θ²ᵏ/(2k)!, coefficients at Q60 for k = 0..7.
    private const long CosPolyC0Q60 = 1152921504606846976L;
    private const long CosPolyC1Q60 = -576460752303423488L;
    private const long CosPolyC2Q60 = 48038396025285291L;
    private const long CosPolyC3Q60 = -1601279867509510L;
    private const long CosPolyC4Q60 = 28594283348384L;
    private const long CosPolyC5Q60 = -317714259426L;
    private const long CosPolyC6Q60 = 2406926208L;
    private const long CosPolyC7Q60 = -13224869L;

    // log2(1 + r) ≈ r·(c1 + r·(c2 + r·(c3 + r·c4))), r ≤ 2⁻⁷: log2(e)/k alternating, at Q61 (truncation ≤ 2⁻³⁶).
    private const long Log2PolyC1Q61 = 3326628274461080623L;
    private const long Log2PolyC2Q61 = -1663314137230540311L;
    private const long Log2PolyC3Q61 = 1108876091487026874L;
    private const long Log2PolyC4Q61 = -831657068615270156L;

    // 2^r ≈ 1 + r·(c1 + r·(c2 + r·(c3 + r·c4))), r ≤ 2⁻⁷: (ln 2)^k/k! at Q62 (truncation ≤ 2⁻⁴⁵).
    private const long Exp2PolyC1Q62 = 3196577161300663915L;
    private const long Exp2PolyC2Q62 = 1107849223398934356L;
    private const long Exp2PolyC3Q62 = 255967521894832113L;
    private const long Exp2PolyC4Q62 = 44355791529079737L;

    // Log2 interval tables over the Q62 mantissa [1, 2), 128 intervals d_i = 1 + i/128: round(2^124 / d_i) and
    // log2(d_i) at Q61 (exact squaring).
    private static ReadOnlySpan<ulong> Log2InverseTableQ62 => [
        4611686018427387904UL, 4575936514408570944UL, 4540737002759274244UL, 4506074888234394288UL,
        4471937957262921604UL, 4438314363599290614UL, 4405192614617206356UL, 4372561558212634457UL,
        4340410370284600380UL, 4308728542764274830UL, 4277505872164533708UL, 4246732448623781667UL,
        4216398645419326084UL, 4186495108926990438UL, 4157012749004969378UL, 4127942729781158404UL,
        4099276460824344804UL, 4071005588680728632UL, 4043121988758257888UL, 4015617757542215318UL,
        3988485205126389539UL, 3961716848045004374UL, 3935305402391371011UL, 3909243777209971203UL,
        3883525068149379288UL, 3858142551364089227UL, 3833089677653932803UL, 3808360066830359043UL,
        3783947502299395203UL, 3759845925851628355UL, 3736049432650035770UL, 3712552266406953784UL,
        3689348814741910323UL, 3666433604712457464UL, 3643801298510528714UL, 3621446689317212587UL,
        3599364697309180803UL, 3577550365810337283UL, 3555998857582564167UL, 3534705451249734441UL,
        3513665537849438403UL, 3492874617507134034UL, 3472328296227680304UL, 3452022282799448256UL,
        3431952385806428208UL, 3412114510743963305UL, 3392504657233940527UL, 3373118916335460867UL,
        3353953467947191203UL, 3335004578297772044UL, 3316268597520818268UL, 3297741957311204758UL,
        3279421168659475843UL, 3261302819661357192UL, 3243383573399481603UL, 3225660165894566403UL,
        3208129404123400281UL, 3190788164101111631UL, 3173633389025299203UL, 3156662087479709367UL,
        3139871331695242828UL, 3123258255866167469UL, 3106820054519503430UL, 3090553980935631684UL,
        3074457345618258603UL, 3058527514811946382UL, 3042761909065493050UL, 3027158001839516163UL,
        3011713318156661488UL, 2996425433292922090UL, 2981291971508614403UL, 2966310604817616340UL,
        2951479051793528259UL, 2936795076411470904UL, 2922256486924285405UL, 2907861134771949023UL,
        2893606913523066920UL, 2879491757847344642UL, 2865513642517988601UL, 2851670581443022472UL,
        2837960626724546402UL, 2824381867745003118UL, 2810932430279550722UL, 2797610475633676074UL,
        2784414199805215338UL, 2771341832669979586UL, 2758391637190213326UL, 2745561908645142566UL,
        2732850973882896536UL, 2720257190593113602UL, 2707778946599567210UL, 2695414659172171926UL,
        2683162774357752962UL, 2671021766328984849UL, 2658990136750926359UL, 2647066414164599335UL,
        2635249153387078802UL, 2623536934927580674UL, 2611928364419051556UL, 2600422072064782607UL,
        2589016712099586192UL, 2577710962265090182UL, 2566503523298720225UL, 2555393118435955202UL,
        2544378492925455395UL, 2533458413556676617UL, 2522631668199596802UL, 2511897065356194263UL,
        2501253433723329033UL, 2490699621766690514UL, 2480234497305485932UL, 2469856947107555028UL,
        2459565876494606882UL, 2449360208957284862UL, 2439238885779775420UL, 2429200865673685810UL,
        2419245124420924802UL, 2409370654525329191UL, 2399576464872787202UL, 2389861580399618023UL,
        2380225041768974402UL, 2370665905055042778UL, 2361183241434822607UL, 2351776136887273513UL,
        2342443691899625602UL, 2333185021180654750UL, 2323999253380730912UL, 2314885530818453536UL,
    ];
    private static ReadOnlySpan<ulong> Log2TableQ61 => [
        0UL, 25888288430367045UL, 51576665303060812UL, 77068194442068270UL,
        102365869772888179UL, 127472617432724975UL, 152391297801646934UL, 177124707458234458UL,
        201675581063062223UL, 226046593173187413UL, 250240359990654796UL, 274259441047877187UL,
        298106340832606437UL, 321783510355074775UL, 345293348659758727UL, 368638204284097356UL,
        391820376666382823UL, 414842117504933834UL, 437705632070560959UL, 460413080474236864UL,
        482966578891793659UL, 505368200747383696UL, 527619977857358834UL, 549723901536146225UL,
        571681923665625736UL, 593495957729445047UL, 615167879813642981UL, 636699529574889513UL,
        658092711177592001UL, 679349194201061303UL, 700470714517878380UL, 721458975144551610UL,
        742315647065507199UL, 763042370031409598UL, 783640753332765647UL, 804112376549725042UL,
        824458790278950699UL, 844681516838395378UL, 864782050950785561UL, 884761860406579922UL,
        904622386707137626UL, 924365045688801178UL, 943991228128569422UL, 963502300332008560UL,
        982899604704022609UL, 1002184460303079509UL, 1021358163379465023UL, 1040421987898113636UL,
        1059377186046543743UL, 1078224988728403495UL, 1096966606043113734UL, 1115603227752075326UL,
        1134136023731890023UL, 1152566144415026524UL, 1170894721218346803UL, 1189122866959891794UL,
        1207251676264310360UL, 1225282225957300859UL, 1243215575449420702UL, 1261052767109605966UL,
        1278794826628730339UL, 1296442763373520450UL, 1313997570731132936UL, 1331460226444687365UL,
        1348831692940038387UL, 1366112917644060180UL, 1383304833294706376UL, 1400408358243099200UL,
        1417424396747892428UL, 1434353839262144106UL, 1451197562712926566UL, 1467956430773893284UL,
        1484631294131014398UL, 1501222990741685322UL, 1517732346087405789UL, 1534160173420219825UL,
        1550507274003100611UL, 1566774437344457898UL, 1582962441426939576UL, 1599072052930693184UL,
        1615104027451247565UL, 1631059109712169480UL, 1646938033772644825UL, 1662741523230129125UL,
        1678470291418207174UL, 1694125041599797115UL, 1709706467155829769UL, 1725215251769529809UL,
        1740652069606421211UL, 1756017585490175504UL, 1771312455074417472UL, 1786537325010599347UL,
        1801692833112050942UL, 1816779608514309788UL, 1831798271831832047UL, 1846749435311181790UL,
        1861633702980793190UL, 1876451670797397222UL, 1891203926789201616UL, 1905891051195910077UL,
        1920513616605664124UL, 1935072188088988369UL, 1949567323329817559UL, 1963999572753681369UL,
        1978369479653120587UL, 1992677580310406158UL, 2006924404117630389UL, 2021110473694237538UL,
        2035236305002059059UL, 2049302407457916768UL, 2063309284043855413UL, 2077257431415064258UL,
        2091147340005545587UL, 2104979494131586345UL, 2118754372093087486UL, 2132472446272804035UL,
        2146134183233547358UL, 2159740043813399627UL, 2173290483218989087UL, 2186785951116873302UL,
        2200226891723076266UL, 2213613743890823949UL, 2226946941196521597UL, 2240226912024014907UL,
        2253454079647176014UL, 2266628862310854103UL, 2279751673310229346UL, 2292822921068607810UL,
    ];

    // Exp2 interval table: round(2^(i/128) · 2^62) for i = 0..127.
    private static ReadOnlySpan<ulong> Exp2TableQ62 => [
        4611686018427387904UL, 4636727017470743990UL, 4661903986662671290UL, 4687217664307630838UL,
        4712668792719003884UL, 4738258118240859931UL, 4763986391269842979UL, 4789854366277176597UL,
        4815862801830788490UL, 4842012460617555202UL, 4868304109465667592UL, 4894738519367117769UL,
        4921316465500308116UL, 4948038727252783086UL, 4974906088244084429UL, 5001919336348730514UL,
        5029079263719320435UL, 5056386666809763567UL, 5083842346398635251UL, 5111447107612659307UL,
        5139201759950318048UL, 5167107117305590498UL, 5195163997991819502UL, 5223373224765708437UL,
        5251735624851448219UL, 5280252029964975318UL, 5308923276338361494UL, 5337750204744335963UL,
        5366733660520940721UL, 5395874493596319735UL, 5425173558513642752UL, 5454631714456164423UL,
        5484249825272419512UL, 5514028759501554901UL, 5543969390398799154UL, 5574072595961070373UL,
        5604339258952723100UL, 5634770266931435031UL, 5665366512274234280UL, 5696128892203667984UL,
        5727058308814112983UL, 5758155669098229379UL, 5789421884973557729UL, 5820857873309260658UL,
        5852464555953009676UL, 5884242859758017993UL, 5916193716610220111UL, 5948318063455599005UL,
        5980616842327661685UL, 6013091000375063951UL, 6045741489889385141UL, 6078569268333053700UL,
        6111575298367424380UL, 6144760547881007893UL, 6178125990017853852UL, 6211672603206087829UL,
        6245401371186603363UL, 6279313283041909767UL, 6313409333225136570UL, 6347690521589195458UL,
        6382157853416100552UL, 6416812339446447902UL, 6451654995909055045UL, 6486686844550761504UL,
        6521908912666391106UL, 6557322233128876982UL, 6592927844419550153UL, 6628726790658592574UL,
        6664720121635655541UL, 6700908892840644341UL, 6737294165494670078UL, 6773877006581169542UL,
        6810658488877194079UL, 6847639690984868337UL, 6884821697363019841UL, 6922205598358980312UL,
        6959792490240559659UL, 6997583475228193593UL, 7035579661527265796UL, 7073782163360605593UL,
        7112192101001162095UL, 7150810600804855741UL, 7189638795243608238UL, 7228677822938551843UL,
        7267928828693418961UL, 7307392963528113063UL, 7347071384712461870UL, 7386965255800153833UL,
        7427075746662858866UL, 7467404033524534369UL, 7507951298995917514UL, 7548718732109204835UL,
        7589707528352920109UL, 7630918889706971581UL, 7672354024677899536UL, 7714014148334315267UL,
        7755900482342532474UL, 7798014255002392130UL, 7840356701283281883UL, 7882929062860351033UL,
        7925732588150922155UL, 7968768532351100432UL, 8012038157472581778UL, 8055542732379660816UL,
        8099283532826439817UL, 8143261841494239668UL, 8187478948029213993UL, 8231936149080167495UL,
        8276634748336579668UL, 8321576056566834961UL, 8366761391656660532UL, 8412192078647772715UL,
        8457869449776733335UL, 8503794844514017006UL, 8549969609603290562UL, 8596395099100905774UL,
        8643072674415606502UL, 8690003704348451461UL, 8737189565132953757UL, 8784631640475438381UL,
        8832331321595618838UL, 8880290007267394103UL, 8928509103859867100UL, 8976990025378585914UL,
        9025734193507008925UL, 9074743037648195108UL, 9124017994966720698UL, 9173560510430823462UL,
    ];

    // Atan2 interval tables over z = min/max ∈ [0, 1] at Q62, 128 intervals z_i = i/128: atan(z_i) and the Taylor
    // coefficients f'(z_i), f''(z_i)/2, f'''(z_i)/6, all at Q61 (cubic truncation ≤ 2⁻³¹ over h ≤ 2⁻⁷).
    private static ReadOnlySpan<long> AtanTableQ61 => [
        0L, 18014032019027246L, 36025865417378412L, 54033303184007600L,
        72034151524184360L, 90026221460299152L, 108007330423874208L, 125975303835894848L,
        143927976672616096L, 161863195014048544L, 179778817572385696L, 197672717197702720L,
        215542782358331872L, 233386918593404448L, 251203049935140960L, 268989120298569152L,
        286743094836456896L, 304462961257357440L, 322146731104782784L, 339792440995643328L,
        357398153816218368L, 374961959874053568L, 392481978004314752L, 409956356629263936L,
        427383274769662080L, 444760943007042112L, 462087604395936128L, 479361535325281856L,
        496581046328371328L, 513744482840845184L, 530850225906371968L, 547896692829786944L,
        564882337777596224L, 581805652325882624L, 598665165955772672L, 615459446496748800L,
        632187100518202368L, 648846773669743360L, 665437150970880768L, 681956957050797824L,
        698404956339037696L, 714779953208007168L, 731080792068293888L, 747306357417866112L,
        763455573846304000L, 779527405995271936L, 795520858476507392L, 811434975748652800L,
        827268841954310144L, 843021580718735616L, 858692354911632256L, 874280366373530496L,
        889784855608267392L, 905205101443100928L, 920540420658007040L, 935790167585718528L,
        950953733684067968L, 966030547082200576L, 981020072102213120L, 995921808757772928L,
        1010735292231251968L, 1025460092330900608L, 1040095812929562752L, 1054642091386410368L,
        1069098597953152896L, 1083465035166144512L, 1097741137225783808L, 1111926669364566656L,
        1126021427205114880L, 1140025236109470848L, 1153937950520904704L, 1167759453299443712L,
        1181489655052292096L, 1195128493460262912L, 1208675932601309696L, 1222131962272191744L,
        1235496597309270272L, 1248769876909386240L, 1261951863951725312L, 1275042644321535232L,
        1288042326236509184L, 1300951039576617472L, 1313768935218113024L, 1326496184372405760L,
        1339132977930449152L, 1351679525813249536L, 1364136056329062912L, 1376502815537806848L,
        1388780066623175424L, 1400968089272910848L, 1413067179067644416L, 1425077646878688256L,
        1436999818275124224L, 1448834032940499200L, 1460580644099413504L, 1472240017954248192L,
        1483812533132255488L, 1495298580143203328L, 1506698560847741952L, 1518012887936631040L,
        1529241984420945664L, 1540386283133350912L, 1551446226240518912L, 1562422264766732032L,
        1573314858128709632L, 1584124473681660416L, 1594851586276560384L, 1605496677828628480L,
        1616060236896962304L, 1626542758275279360L, 1636944742593698048L, 1647266695931473664L,
        1657509129440605184L, 1667672558980201216L, 1677757504761499392L, 1687764491003412480L,
        1697694045598473728L, 1707546699789041152L, 1717322987853615872L, 1727023446803126272L,
        1736648616087014656L, 1746199037308969472L, 1755675253952136448L, 1765077811113634304L,
        1774407255248205824L, 1783664133920824576L, 1792848995568082432L, 1801962389268170496L,
    ];
    private static ReadOnlySpan<long> AtanDerivative1TableQ61 => [
        2305843009213693952L, 2305702280314748960L, 2305280196665679870L, 2304577067221201837L,
        2303593406277875714L, 2302329932534411708L, 2300787567780582321L, 2298967435219202928L,
        2296870857426870240L, 2294499353960349937L, 2291854638616668388L, 2288938616356083715L,
        2285753379898182582L, 2282301206002365838L, 2278584551444943408L, 2274606048705952298L,
        2270368501379637122L, 2265874879323286854L, 2261128313559801395L, 2256132090949964868L,
        2250889648650927175L, 2245404568377840220L, 2239680570485959314L, 2233721507890803625L,
        2227531359844172271L, 2221114225583935664L, 2214474317875566337L, 2207615956463341419L,
        2200543561449042504L, 2193261646615800390L, 2185774812714485172L, 2178087740729729704L,
        2170205185142300190L, 2162131967204095559L, 2153872968241571363L, 2145433123002848640L,
        2136817413063187880L, 2128030860302887496L, 2119078520471009744L, 2109965476847649355L,
        2100696834016746092L, 2091277711760706433L, 2081713239087346358L, 2072008548398900988L,
        2062168769812072146L, 2052199025637305759L, 2042104425024711444L, 2031890058783260459L,
        2021560994379128944L, 2011122271118294475L, 2000578895517748449L, 1989935836868957688L,
        1979198022996498413L, 1968370336214096895L, 1957457609479645684L, 1946464622750124257L,
        1935396099536739842L, 1924256703660019442L, 1913051036204028849L, 1901783632668369580L,
        1890458960316110974L, 1879081415715352485L, 1867655322471680923L, 1856184929148389019L,
        1844674407370955162L, 1833127850111949231L, 1821549270152225733L, 1809942598713992321L,
        1798311684261098710L, 1786660291461677073L, 1774992100308079389L, 1763310705388899030L,
        1751619615307731904L, 1739922252243225796L, 1728221951644883884L, 1716521962059028657L,
        1704825445079294301L, 1693135475415997925L, 1681455041078741397L, 1669787043666614882L,
        1658134298760409134L, 1646499536411294910L, 1634885401720493410L, 1623294455504540098L,
        1611729175040834544L, 1600191954888269800L, 1588685107777845320L, 1577210865568286299L,
        1565771380261818705L, 1554368725075382090L, 1543004895562700609L, 1531681810782775662L,
        1520401314510510371L, 1509165176485325838L, 1497975093693781194L, 1486832691682363010L,
        1475739525896764129L, 1464697083044126767L, 1453706782474879241L, 1442769977580949464L,
        1431887957207290847L, 1421061947073807098L, 1410293111204911218L, 1399582553364100386L,
        1388931318491072122L, 1378340394139047820L, 1367810711910107231L, 1357343148886471516L,
        1346938529055802970L, 1336597624728716140L, 1326321157946817923L, 1316109801879713002L,
        1305964182209525778L, 1295884878501600580L, 1285872425560148458L, 1275927314767711227L,
        1266049995407411585L, 1256240875967052230L, 1246500325424216765L, 1236828674511611122L,
        1227226216961966012L, 1217693210731898846L, 1208229879204207551L, 1198836412368138918L,
        1189512967977240608L, 1180259672684468797L, 1171076623154282756L, 1161963887151513601L,
    ];
    private static ReadOnlySpan<long> AtanDerivative2TableQ61 => [
        0L, -18012199687536641L, -36011211273273984L, -53983870787100684L,
        -71917062439894656L, -89797742508502864L, -107612962975364630L, -125349894843084393L,
        -142995851046030832L, -160538308883226427L, -177964931899377307L, -195263591143857369L,
        -212422385740779795L, -229429662706937519L, -246274035958343703L, -262944404450323825L,
        -279429969400570723L, -295720250549239621L, -311805101414997750L, -327674723510917561L,
        -343319679489178597L, -358730905188685558L, -373899720564883888L, -388817839486225145L,
        -403477378386868940L, -417870863770274215L, -431991238563299224L, -445831867325267805L,
        -459386540321142144L, -472649476472444183L, -485615325203866180L, -498279167207585325L,
        -510636514151129456L, -522683307358215512L, -534415915495286122L, -545831131299492413L,
        -556926167386604624L, -567698651179771035L, -578146619002187087L, -588268509378579480L,
        -598063155591956183L, -607529777543326175L, -616667972963057859L, -625477708023229915L,
        -633959307400741830L, -642113443841103871L, -649941127272729783L, -657443693521222768L,
        -664622792672590338L, -671480377133560977L, -678018689436220614L, -684240249833055348L,
        -690147843727194753L, -695744508981213637L, -701033523146285543L, -706018390651804563L,
        -710702829993819221L, -715090760958768494L, -719186291917090853L, -722993707219306674L,
        -726517454725166748L, -729762133494427864L, -732732481665773176L, -735433364548352039L,
        -737869762948382065L, -740046761751245456L, -741969538777531485L, -743643353929535675L,
        -745073538642831454L, -746265485655688433L, -747224639107329042L, -747956484974297054L,
        -748466541852562001L, -748760352091406155L, -748843473283638616L, -748721470115256264L,
        -748399906576325585L, -747884338533593670L, -747180306664151420L, -746293329748367279L,
        -745228898319285004L, -743992468664733303L, -742589457177527213L, -741025235048349332L,
        -739305123295181443L, -737434388122511560L, -735418236602965571L, -733261812673503787L,
        -730970193437878228L, -728548385766663352L, -726001323185848351L, -723333863044709950L,
        -720550783953467890L, -717656783481057755L, -714656476103234549L, -711554391391142280L,
        -708354972430446782L, -705062574461128133L, -701681463728062383L, -698215816532587039L,
        -694669718475338191L, -691047163880766529L, -687352055393882368L, -683588203739943670L,
        -679759327637983532L, -675869053859272600L, -671920917422025159L, -667918361913883300L,
        -663864739933949667L, -659763313646384136L, -655617255437831637L, -651429648671205794L,
        -647203488528614545L, -642941682936478263L, -638647053566156784L, -634322336903668116L,
        -629970185382347427L, -625593168572559246L, -621193774422837865L, -616774410547089887L,
        -612337405552748114L, -607885010405016900L, -603419399822595149L, -598942673700503891L,
        -594456858555880445L, -589963908992830165L, -585465709182649482L, -580964074355950085L,
    ];
    private static ReadOnlySpan<long> AtanDerivative3TableQ61 => [
        -768614336404564651L, -768332904372634080L, -767489123379437524L, -766084536973569699L,
        -764121711881703485L, -761604229260812848L, -758536672518407239L, -754924611767253013L,
        -750774584999134641L, -746094076079612633L, -740891489682332020L, -735176123297099281L,
        -728958136460561317L, -722248517371783277L, -715059047067240554L, -707402261340635307L,
        -699291410602453911L, -690740417882247749L, -681763835183208978L, -672376798403702963L,
        -662594981044002264L, -652434546918549521L, -641912102094678391L, -631044646277875782L,
        -619849523861420459L, -608344374854639543L, -596547085899152786L, -584475741576401638L,
        -572148576202571035L, -559583926298798691L, -546800183915427380L, -533815750979092864L,
        -520648994820759446L, -507318205031524683L, -493841551781223193L, -480237045722675300L,
        -466522499591956519L, -452715491602413231L, -438833330717419633L, -424893023874158280L,
        -410911245218103228L, -396904307395477648L, -382888134938826804L, -368878239769066418L,
        -354889698826002149L, -340937133828427841L, -327034693154550278L, -313196035823701298L,
        -299434317551121370L, -285762178839062353L, -272191735059583825L, -258734568477223448L,
        -245401722153216627L, -232203695667127777L, -219150442586632335L, -206251369611747460L,
        -193515337316037583L, -180950662404201409L, -168565121402958589L, -156365955700272694L,
        -144359877846644644L, -132553079031457582L, -120951237647118129L, -109559528853986510L,
        -98382635059784275L, -87424757228277733L, -76689626933521737L, -66180519077776049L,
        -55900265193339385L, -45851267250949101L, -36035511900032886L, -26454585068939326L,
        -17109686856284525L, -8001646647701300L, 869061604463592L, 9502303997617869L,
        17898271247274719L, 26057463259574939L, 33980673756831228L, 41668975001519580L,
        49123702660746877L, 56346440849875263L, 63339007390697860L, 70103439316350395L,
        76641978652021193L, 82957058497497998L, 89051289434672867L, 94927446280323272L,
        100588455201804319L, 106037381210728320L, 111277416047277204L, 116311866465492662L,
        121144142927719864L, 125777748714343300L, 130216269453047334L, 134463363070058181L,
        138522750164176260L, 142398204802885727L, 146093545738428462L, 149612628040449313L,
        152959335140654212L, 156137571283868638L, 159151254378936431L, 162004309242053479L,
        164700661224382616L, 167244230215140228L, 169638925010776642L, 171888638040386512L,
        173997240437076974L, 175968577444685661L, 177806464148972674L, 179514681522205770L,
        181096972769911529L, 182557039968472798L, 183898540982209744L, 185125086648584304L,
        186240238220211651L, 187247505052443558L, 188150342525403689L, 188952150189500257L,
        189656270123613923L, 190265985495355104L, 190784519313002077L, 191215033358966617L,
        191560627294884840L, 191824337928694990L, 192009138634338858L, 192117938915007325L,
    ];

    /// <summary>Converts a <see cref="FixedQ4816"/> to the nearest <see cref="double"/>.</summary>
    /// <param name="value">The value to convert.</param>
    /// <returns>The real value of <paramref name="value"/> as a <see cref="double"/>; precision may be lost for large magnitudes.</returns>
    public static explicit operator double(FixedQ4816 value) =>
        (value.Value * RawOneInverse);
    /// <summary>Returns the negation of <paramref name="value"/>, wrapping only at <see cref="MinValue"/>.</summary>
    /// <param name="value">The value to negate.</param>
    /// <returns>The arithmetic negation of <paramref name="value"/>.</returns>
    public static FixedQ4816 operator -(FixedQ4816 value) =>
        new(Value: unchecked(-value.Value));
    /// <summary>Returns the negation of <paramref name="value"/>, throwing when the result is not representable.</summary>
    /// <param name="value">The value to negate.</param>
    /// <returns>The arithmetic negation of <paramref name="value"/>.</returns>
    /// <exception cref="OverflowException"><paramref name="value"/> is <see cref="MinValue"/>.</exception>
    public static FixedQ4816 operator checked -(FixedQ4816 value) =>
        new(Value: checked(-value.Value));
    /// <summary>Returns <paramref name="value"/> increased by one, wrapping on overflow.</summary>
    /// <param name="value">The value to increment.</param>
    /// <returns><paramref name="value"/> plus <c>1.0</c>.</returns>
    public static FixedQ4816 operator ++(FixedQ4816 value) =>
        new(Value: unchecked((value.Value + RawOne)));
    /// <summary>Returns <paramref name="value"/> increased by one, throwing when the result is not representable.</summary>
    /// <param name="value">The value to increment.</param>
    /// <returns><paramref name="value"/> plus <c>1.0</c>.</returns>
    /// <exception cref="OverflowException">The result exceeds <see cref="MaxValue"/>.</exception>
    public static FixedQ4816 operator checked ++(FixedQ4816 value) =>
        new(Value: checked(value.Value + RawOne));
    /// <summary>Returns <paramref name="value"/> decreased by one, wrapping on underflow.</summary>
    /// <param name="value">The value to decrement.</param>
    /// <returns><paramref name="value"/> minus <c>1.0</c>.</returns>
    public static FixedQ4816 operator --(FixedQ4816 value) =>
        new(Value: unchecked((value.Value - RawOne)));
    /// <summary>Returns <paramref name="value"/> decreased by one, throwing when the result is not representable.</summary>
    /// <param name="value">The value to decrement.</param>
    /// <returns><paramref name="value"/> minus <c>1.0</c>.</returns>
    /// <exception cref="OverflowException">The result is less than <see cref="MinValue"/>.</exception>
    public static FixedQ4816 operator checked --(FixedQ4816 value) =>
        new(Value: checked(value.Value - RawOne));
    /// <summary>Adds two values, wrapping on overflow.</summary>
    /// <param name="x">The first addend.</param>
    /// <param name="y">The second addend.</param>
    /// <returns>The sum <c><paramref name="x"/> + <paramref name="y"/></c>.</returns>
    public static FixedQ4816 operator +(FixedQ4816 x, FixedQ4816 y) =>
        new(Value: unchecked((x.Value + y.Value)));
    /// <summary>Adds two values, throwing when the result is not representable.</summary>
    /// <param name="x">The first addend.</param>
    /// <param name="y">The second addend.</param>
    /// <returns>The sum <c><paramref name="x"/> + <paramref name="y"/></c>.</returns>
    /// <exception cref="OverflowException">The sum is outside the representable range.</exception>
    public static FixedQ4816 operator checked +(FixedQ4816 x, FixedQ4816 y) =>
        new(Value: checked(x.Value + y.Value));
    /// <summary>Subtracts <paramref name="y"/> from <paramref name="x"/>, wrapping on underflow.</summary>
    /// <param name="x">The minuend.</param>
    /// <param name="y">The subtrahend.</param>
    /// <returns>The difference <c><paramref name="x"/> − <paramref name="y"/></c>.</returns>
    public static FixedQ4816 operator -(FixedQ4816 x, FixedQ4816 y) =>
        new(Value: unchecked((x.Value - y.Value)));
    /// <summary>Subtracts two values, throwing when the result is not representable.</summary>
    /// <param name="x">The minuend.</param>
    /// <param name="y">The subtrahend.</param>
    /// <returns>The difference <c><paramref name="x"/> − <paramref name="y"/></c>.</returns>
    /// <exception cref="OverflowException">The difference is outside the representable range.</exception>
    public static FixedQ4816 operator checked -(FixedQ4816 x, FixedQ4816 y) =>
        new(Value: checked(x.Value - y.Value));
    /// <summary>Multiplies two values in fixed point, rounding the result to nearest with ties to even and wrapping on overflow.</summary>
    /// <param name="x">The multiplicand.</param>
    /// <param name="y">The multiplier.</param>
    /// <returns>The rounded product <c><paramref name="x"/> × <paramref name="y"/></c>.</returns>
    public static FixedQ4816 operator *(FixedQ4816 x, FixedQ4816 y) {
        // The raw product is X·Y·2^32; divide by 2^16 and round to nearest, ties to even. Rounding the magnitude
        // and re-applying the sign equals rounding the signed value (the integer neighbors share parity).
        // Measured 2026-07 (.NET 10): this Int128 form is ~2x faster than both Math.BigMul rewrites; re-measure
        // before replacing.
        var product = ((Int128)x.Value * y.Value);
        var negative = (product < Int128.Zero);
        var magnitude = (UInt128)(negative
            ? -product
            : product);
        var truncated = ((ulong)(magnitude >> FractionBitCount));
        var remainder = (ulong)magnitude & FractionBitMask;

        if (
            (remainder > RawHalf) ||
            ((remainder == RawHalf) && ((truncated & 1UL) != 0UL))
        ) {
            ++truncated;
        }

        var result = ((long)truncated);

        return new(Value: (negative
            ? unchecked(-result)
            : result));
    }
    /// <summary>Multiplies two values in fixed point, rounding to nearest with ties to even and throwing when the rounded result is not representable.</summary>
    /// <param name="x">The multiplicand.</param>
    /// <param name="y">The multiplier.</param>
    /// <returns>The rounded product <c><paramref name="x"/> × <paramref name="y"/></c>.</returns>
    /// <exception cref="OverflowException">The rounded product is outside the representable range.</exception>
    public static FixedQ4816 operator checked *(FixedQ4816 x, FixedQ4816 y) {
        var product = ((Int128)x.Value * y.Value);
        var negative = (product < Int128.Zero);
        var magnitude = (UInt128)(negative
            ? -product
            : product);
        var roundedMagnitude = (magnitude >> FractionBitCount);
        var remainder = ((ulong)magnitude & FractionBitMask);

        if (
            (remainder > RawHalf) ||
            ((remainder == RawHalf) && ((roundedMagnitude & UInt128.One) != UInt128.Zero))
        ) {
            ++roundedMagnitude;
        }

        return FromCheckedMagnitude(magnitude: roundedMagnitude, negative: negative);
    }
    /// <summary>Divides <paramref name="x"/> by <paramref name="y"/> in fixed point, rounding the result to nearest with ties to even and wrapping on overflow.</summary>
    /// <param name="x">The dividend.</param>
    /// <param name="y">The divisor.</param>
    /// <returns>The rounded quotient <c><paramref name="x"/> ÷ <paramref name="y"/></c>.</returns>
    /// <exception cref="DivideByZeroException"><paramref name="y"/> is zero.</exception>
    public static FixedQ4816 operator /(FixedQ4816 x, FixedQ4816 y) {
        // result = round((x.Value << 16) / y.Value), ties to even. Magnitude divide at 128-bit width: hardware
        // 128-by-64 division when the quotient fits 64 bits (dividend high word below the divisor), UInt128
        // otherwise. The 2r vs d rounding compare is evaluated as r vs d − r, which cannot overflow; the combined
        // sign is re-applied afterward (parity-symmetric, so both signs round identically).
        var signX = (x.Value >> 63);
        var signY = (y.Value >> 63);
        var xMagnitude = unchecked((ulong)((x.Value ^ signX) - signX));
        var yMagnitude = unchecked((ulong)((y.Value ^ signY) - signY));
        var high = (xMagnitude >> IntegerBitCount);
        ulong quotient;
        ulong remainder;

        if (
            X86Base.X64.IsSupported &&
            (high < yMagnitude)
        ) {
#pragma warning disable SYSLIB5004
            (quotient, remainder) = X86Base.X64.DivRem(
                lower: unchecked((xMagnitude << FractionBitCount)),
                upper: high,
                divisor: yMagnitude
            );
#pragma warning restore SYSLIB5004
        } else {
            var dividend = (((UInt128)xMagnitude) << FractionBitCount);
            var quotient128 = (dividend / yMagnitude);

            quotient = unchecked((ulong)quotient128);
            remainder = ((ulong)(dividend - (quotient128 * yMagnitude)));
        }

        if (
            (remainder > (yMagnitude - remainder)) ||
            ((remainder == (yMagnitude - remainder)) && ((quotient & 1UL) != 0UL))
        ) {
            ++quotient;
        }

        var result = unchecked((long)quotient);
        var resultSign = signX ^ signY;

        return new(Value: unchecked(((result ^ resultSign) - resultSign)));
    }
    /// <summary>Divides two values in fixed point, rounding to nearest with ties to even and throwing when the rounded result is not representable.</summary>
    /// <param name="x">The dividend.</param>
    /// <param name="y">The divisor.</param>
    /// <returns>The rounded quotient <c><paramref name="x"/> ÷ <paramref name="y"/></c>.</returns>
    /// <exception cref="DivideByZeroException"><paramref name="y"/> is zero.</exception>
    /// <exception cref="OverflowException">The rounded quotient is outside the representable range.</exception>
    public static FixedQ4816 operator checked /(FixedQ4816 x, FixedQ4816 y) {
        var signX = (x.Value >> 63);
        var signY = (y.Value >> 63);
        var xMagnitude = unchecked((ulong)((x.Value ^ signX) - signX));
        var yMagnitude = unchecked((ulong)((y.Value ^ signY) - signY));
        var dividend = (((UInt128)xMagnitude) << FractionBitCount);
        var quotient = (dividend / yMagnitude);
        var remainder = ((ulong)(dividend - (quotient * yMagnitude)));

        if (
            (remainder > (yMagnitude - remainder)) ||
            ((remainder == (yMagnitude - remainder)) && ((quotient & UInt128.One) != UInt128.Zero))
        ) {
            ++quotient;
        }

        return FromCheckedMagnitude(magnitude: quotient, negative: ((signX ^ signY) != 0L));
    }

    private static FixedQ4816 FromCheckedMagnitude(UInt128 magnitude, bool negative) {
        var negativeLimit = (UInt128.One << 63);

        if (negative) {
            if (magnitude > negativeLimit) { throw new OverflowException(); }
            if (magnitude == negativeLimit) { return MinValue; }

            return new(Value: -checked((long)magnitude));
        }

        return new(Value: checked((long)magnitude));
    }
    /// <summary>Returns the remainder of dividing the raw storage of <paramref name="x"/> by that of <paramref name="y"/>.</summary>
    /// <param name="x">The dividend.</param>
    /// <param name="y">The divisor.</param>
    /// <returns>The fixed-point remainder <c><paramref name="x"/> mod <paramref name="y"/></c>, with the sign of <paramref name="x"/>.</returns>
    /// <exception cref="DivideByZeroException"><paramref name="y"/> is zero.</exception>
    public static FixedQ4816 operator %(FixedQ4816 x, FixedQ4816 y) {
        // Every integer is exactly divisible by ±1. Bypass the CLR's signed-division overflow trap for
        // long.MinValue % -1 while preserving the ordinary divide-by-zero exception for a zero divisor.
        if ((y.Value == 1L) || (y.Value == -1L)) {
            return Zero;
        }

        return new(Value: (x.Value % y.Value));
    }
    /// <summary>Indicates whether <paramref name="x"/> is less than <paramref name="y"/>.</summary>
    /// <param name="x">The first value to compare.</param>
    /// <param name="y">The second value to compare.</param>
    /// <returns><see langword="true"/> when <paramref name="x"/> is less than <paramref name="y"/>; otherwise <see langword="false"/>.</returns>
    public static bool operator <(FixedQ4816 x, FixedQ4816 y) =>
        (x.Value < y.Value);
    /// <summary>Indicates whether <paramref name="x"/> is less than or equal to <paramref name="y"/>.</summary>
    /// <param name="x">The first value to compare.</param>
    /// <param name="y">The second value to compare.</param>
    /// <returns><see langword="true"/> when <paramref name="x"/> is less than or equal to <paramref name="y"/>; otherwise <see langword="false"/>.</returns>
    public static bool operator <=(FixedQ4816 x, FixedQ4816 y) =>
        (x.Value <= y.Value);
    /// <summary>Indicates whether <paramref name="x"/> is greater than <paramref name="y"/>.</summary>
    /// <param name="x">The first value to compare.</param>
    /// <param name="y">The second value to compare.</param>
    /// <returns><see langword="true"/> when <paramref name="x"/> is greater than <paramref name="y"/>; otherwise <see langword="false"/>.</returns>
    public static bool operator >(FixedQ4816 x, FixedQ4816 y) =>
        (x.Value > y.Value);
    /// <summary>Indicates whether <paramref name="x"/> is greater than or equal to <paramref name="y"/>.</summary>
    /// <param name="x">The first value to compare.</param>
    /// <param name="y">The second value to compare.</param>
    /// <returns><see langword="true"/> when <paramref name="x"/> is greater than or equal to <paramref name="y"/>; otherwise <see langword="false"/>.</returns>
    public static bool operator >=(FixedQ4816 x, FixedQ4816 y) =>
        (x.Value >= y.Value);

    /// <summary>Gets the additive identity of the type, zero.</summary>
    public static FixedQ4816 AdditiveIdentity => default;
    /// <summary>Gets the smallest representable positive value, one unit in the last place (<c>2⁻¹⁶</c>).</summary>
    public static FixedQ4816 Epsilon => new(Value: RawEpsilon);
    /// <summary>Gets the largest representable value.</summary>
    public static FixedQ4816 MaxValue => new(Value: long.MaxValue);
    /// <summary>Gets the smallest (most negative) representable value.</summary>
    public static FixedQ4816 MinValue => new(Value: long.MinValue);
    /// <summary>Gets the multiplicative identity of the type, one.</summary>
    public static FixedQ4816 MultiplicativeIdentity => new(Value: RawOne);
    /// <summary>Gets the value negative one.</summary>
    public static FixedQ4816 NegativeOne => new(Value: -RawOne);
    /// <summary>Gets the value one.</summary>
    public static FixedQ4816 One => new(Value: RawOne);
    /// <summary>Gets the value zero.</summary>
    public static FixedQ4816 Zero => default;

    /// <summary>Returns the absolute value of <paramref name="value"/>.</summary>
    /// <param name="value">The value whose absolute value is returned.</param>
    /// <returns>The non-negative magnitude of <paramref name="value"/>.</returns>
    /// <exception cref="OverflowException"><paramref name="value"/> is <see cref="MinValue"/>, whose magnitude is unrepresentable.</exception>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ4816 Abs(FixedQ4816 value) =>
        new(Value: Math.Abs(value: value.Value));
    /// <summary>Returns the smallest integral value greater than or equal to <paramref name="value"/>.</summary>
    /// <param name="value">The value to round up.</param>
    /// <returns><paramref name="value"/> rounded toward positive infinity to a whole number.</returns>
    /// <exception cref="OverflowException">The ceiling exceeds <see cref="MaxValue"/>.</exception>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ4816 Ceiling(FixedQ4816 value) {
        var floor = value.Value & IntegerBitMask;

        return new(Value: (((value.Value & (long)FractionBitMask) != 0L)
            ? checked((floor + RawOne))
            : floor));
    }
    /// <summary>Restricts <paramref name="value"/> to the inclusive range <c>[<paramref name="minimum"/>, <paramref name="maximum"/>]</c>.</summary>
    /// <param name="value">The value to clamp.</param>
    /// <param name="minimum">The inclusive lower bound.</param>
    /// <param name="maximum">The inclusive upper bound.</param>
    /// <returns><paramref name="minimum"/> when <paramref name="value"/> is below it, <paramref name="maximum"/> when above it, otherwise <paramref name="value"/>.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ4816 Clamp(FixedQ4816 value, FixedQ4816 minimum, FixedQ4816 maximum) =>
        new(Value: Math.Clamp(
        value: value.Value,
        max: maximum.Value,
        min: minimum.Value
    ));
    /// <summary>Returns the magnitude of <paramref name="value"/> carrying the sign of <paramref name="sign"/>.</summary>
    /// <param name="value">The value whose magnitude is taken.</param>
    /// <param name="sign">The value whose sign is applied; a zero <paramref name="sign"/> counts as non-negative.</param>
    /// <returns><paramref name="value"/> made negative when <paramref name="sign"/> is negative and non-negative otherwise.</returns>
    /// <exception cref="OverflowException"><paramref name="value"/> is <see cref="MinValue"/> and <paramref name="sign"/> is non-negative, so the requested positive magnitude is unrepresentable.</exception>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ4816 CopySign(FixedQ4816 value, FixedQ4816 sign) {
        if ((value.Value == long.MinValue) && (sign.Value >= 0L)) {
            throw new OverflowException(message: $"The positive magnitude of {nameof(FixedQ4816)}.{nameof(MinValue)} is not representable.");
        }

        // Take the magnitude then re-apply the target sign, both branchless (the divide operator's idiom).
        var magnitudeSign = (value.Value >> 63);
        var magnitude = unchecked(((value.Value ^ magnitudeSign) - magnitudeSign));
        var targetSign = (sign.Value >> 63);

        return new(Value: unchecked(((magnitude ^ targetSign) - targetSign)));
    }
    /// <summary>Returns the largest integral value less than or equal to <paramref name="value"/>.</summary>
    /// <param name="value">The value to round down.</param>
    /// <returns><paramref name="value"/> with its fractional bits cleared (rounded toward negative infinity).</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ4816 Floor(FixedQ4816 value) =>
        new(Value: value.Value & IntegerBitMask);
    /// <summary>Returns the fractional part of <paramref name="value"/> — the non-negative portion above its floor.</summary>
    /// <param name="value">The value whose fractional part is returned.</param>
    /// <returns>A value in <c>[0, 1)</c> equal to <c><paramref name="value"/> − Floor(<paramref name="value"/>)</c>.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ4816 Fractional(FixedQ4816 value) =>
        new(Value: value.Value & (long)FractionBitMask);
    /// <summary>Converts a <see cref="double"/> to a <see cref="FixedQ4816"/>, rounding to nearest with ties to even.</summary>
    /// <param name="value">The value to convert.</param>
    /// <returns>The nearest representable <see cref="FixedQ4816"/>, clamped to <c>[<see cref="MinValue"/>, <see cref="MaxValue"/>]</c>. Not-a-number clamps to zero.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ4816 FromDouble(double value) {
        var scaled = double.Round(
            x: (value * RawOne),
            mode: MidpointRounding.ToEven
        );

        // Saturate to the exact extremes rather than casting the nearest-representable clamp: the largest double below
        // 2^63 (ScaledMaximum) sits a whole ULP short of MaxValue, so a double clamp alone can never reach it.
        if (double.IsNaN(d: scaled)) { return Zero; }
        if (scaled > ScaledMaximum) { return MaxValue; }
        if (scaled <= ScaledMinimum) { return MinValue; }

        return new(Value: unchecked((long)scaled));
    }
    /// <summary>Constructs a <see cref="FixedQ4816"/> from a whole number.</summary>
    /// <param name="value">The integer to represent. Its magnitude must fit the integer range of the format.</param>
    /// <returns>The fixed-point value equal to <paramref name="value"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is outside the integer range of the format.</exception>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ4816 FromInteger(long value) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            value: value,
            other: MaxIntegerValue
        );
        ArgumentOutOfRangeException.ThrowIfLessThan(
            value: value,
            other: MinIntegerValue
        );

        return new(Value: (value << FractionBitCount));
    }
    /// <summary>Constructs a <see cref="FixedQ4816"/> directly from a raw storage bit pattern.</summary>
    /// <param name="value">The pre-scaled raw value to wrap, interpreted as the real number <c><paramref name="value"/> / 2¹⁶</c>.</param>
    /// <returns>A <see cref="FixedQ4816"/> whose <see cref="Value"/> equals <paramref name="value"/>.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ4816 FromRawBits(long value) =>
        new(Value: value);
    /// <summary>Returns two raised to the power <paramref name="value"/>.</summary>
    /// <param name="value">The exponent.</param>
    /// <returns><c>2^value</c>, rounded to nearest: exact at whole-number exponents, saturating to <see cref="MaxValue"/> at exponents of 47 and above, and zero below −17.5 (where the true result rounds under half a ULP).</returns>
    /// <remarks>Pure integer arithmetic (a 128-interval mantissa table plus a quartic residual polynomial);
    /// bit-identical across machines. Maximum observed error is 0.64 ULP for results below 2²⁷; relative error stays
    /// below ~2⁻⁴² for larger results, where absolute ULP correctness is not representable. The inverse of
    /// <see cref="Log2"/>.</remarks>
    public static FixedQ4816 Exp2(FixedQ4816 value) {
        if (value.Value >= (47L << FractionBitCount)) {
            return MaxValue;
        }

        // 2^(k + f) = 2^k · 2^(i/128) · 2^r with f's top seven bits selecting the interval and r < 2^-7 residual.
        var k = (value.Value >> FractionBitCount);
        var f = value.Value & (long)FractionBitMask;
        var index = ((int)(f >> 9));
        var r = ((f & 0x1FFL) << 46);
        var acc = Exp2PolyC4Q62;

        acc = (Exp2PolyC3Q62 + BigMulShift62(
            x: r,
            y: acc
        ));
        acc = (Exp2PolyC2Q62 + BigMulShift62(
            x: r,
            y: acc
        ));
        acc = (Exp2PolyC1Q62 + BigMulShift62(
            x: r,
            y: acc
        ));

        var mantissa = BigMulShift62(
            x: ((long)Exp2TableQ62[index]),
            y: ((1L << 62) + BigMulShift62(
                x: r,
                y: acc
            ))
        );
        // The shift compares in LONG before narrowing: a deeply negative exponent's shift wraps an int cast back
        // into shifting range and would return ~1 instead of 0.
        var shift = (46L - k);

        if (shift >= 64L) {
            return Zero;
        }

        // The round-shift runs unsigned: the mantissa sits just below 2^63, so adding the rounding half would
        // overflow a signed sum for large shifts.
        return new(Value: ((shift <= 0L)
            ? mantissa
            : ((long)((((ulong)mantissa) + (1UL << (((int)shift) - 1))) >> ((int)shift)))));
    }
    /// <summary>Returns the base-2 logarithm of <paramref name="value"/>.</summary>
    /// <param name="value">The value whose logarithm is returned.</param>
    /// <returns>The base-2 logarithm rounded to the nearest representable value, in <c>[−16, 47)</c>; non-positive inputs yield <see cref="MinValue"/>.</returns>
    /// <remarks>Pure integer arithmetic (a 128-interval mantissa table plus a quartic residual polynomial); bit-identical across machines. Maximum observed error is 0.50 ULP.</remarks>
    public static FixedQ4816 Log2(FixedQ4816 value) {
        if (value.Value <= 0L) {
            return MinValue;
        }

        // log2(raw/2^16) = ilog2(raw) − 16 + log2(mantissa); the fraction is computed at Q61 and rounded to Q16.
        var raw = ((ulong)value.Value);
        var integerPart = BitOperations.Log2(value: raw);
        var fraction = Log2FractionQ61(mantissaQ62: (raw << (62 - integerPart)));

        return new(Value: ((((long)(integerPart - FractionBitCount)) << 16) + ((fraction + (1L << 44)) >> 45)));
    }
    /// <summary>Returns <paramref name="x"/> raised to the power <paramref name="y"/>.</summary>
    /// <param name="x">The base; negative bases yield <see cref="Zero"/> (no general real power).</param>
    /// <param name="y">The exponent.</param>
    /// <returns><c>x^y</c>. A zero base yields <see cref="One"/>, <see cref="Zero"/>, or <see cref="MaxValue"/> for a zero, positive, or negative exponent respectively.</returns>
    /// <remarks>Whole-number exponents within ±32 compute by squaring (after a range check against
    /// <see cref="Log2"/>): non-negative exponents square the base exactly; negative exponents square the
    /// correctly-rounded inverse base. Other exponents compute as <c>Exp2(y·Log2(x))</c>, whose relative error
    /// grows with <c>|y·log₂ x|</c> because the intermediate exponent quantizes to Q16 (about 2⁻¹⁷ per
    /// unit).</remarks>
    public static FixedQ4816 Pow(FixedQ4816 x, FixedQ4816 y) {
        if (x.Value <= 0L) {
            if (x.Value == 0L) {
                return ((y.Value == 0L)
                    ? One
                    : ((y.Value > 0L)
                        ? Zero
                        : MaxValue));
            }

            return Zero;
        }

        var log = Log2(value: x);

        if ((y.Value & (long)FractionBitMask) == 0L) {
            var exponent = (y.Value >> FractionBitCount);

            if (
                (exponent >= -32L) &&
                (exponent <= 32L)
            ) {
                // The log-scaled magnitude bounds the FINAL result, keeping the squaring loop off wrap territory.
                // The 32-raw slack absorbs the rounded log's worst error (0.5 ULP × |exponent|), which would
                // otherwise admit results marginally past 2^47 and wrap the sign.
                var magnitude = (log.Value * exponent);

                if (magnitude >= ((47L << FractionBitCount) - 32L)) {
                    return MaxValue;
                }

                if (magnitude < (-18L << FractionBitCount)) {
                    return Zero;
                }

                // A negative exponent squares the correctly-rounded INVERSE base: inverting the positive power
                // afterwards would amplify the quantization of a small intermediate (and could divide by a
                // rounded-to-zero one).
                var result = One;
                var baseValue = ((exponent < 0L)
                    ? (One / x)
                    : x);
                var remaining = ((exponent < 0L)
                    ? -exponent
                    : exponent);

                while (remaining > 0L) {
                    if ((remaining & 1L) != 0L) {
                        result *= baseValue;
                    }

                    remaining >>= 1;

                    if (remaining > 0L) {
                        baseValue *= baseValue;
                    }
                }

                return result;
            }
        }

        // Form and round y·log2(x) at full width before applying Exp2's saturation gates. Using the public
        // wrapping multiplication here can turn an exponent outside the Q48.16 range into an arbitrary value.
        var exponentProduct = ((Int128)y.Value * log.Value);
        var exponentNegative = (exponentProduct < Int128.Zero);
        var exponentMagnitude = (UInt128)(exponentNegative
            ? -exponentProduct
            : exponentProduct);
        var roundedExponentMagnitude = (exponentMagnitude >> FractionBitCount);
        var exponentRemainder = ((ulong)exponentMagnitude & FractionBitMask);

        if (
            (exponentRemainder > RawHalf) ||
            ((exponentRemainder == RawHalf) && ((roundedExponentMagnitude & UInt128.One) != UInt128.Zero))
        ) {
            ++roundedExponentMagnitude;
        }

        var exponentRaw = (exponentNegative
            ? -(Int128)roundedExponentMagnitude
            : (Int128)roundedExponentMagnitude);

        if (exponentRaw >= (47L << FractionBitCount)) {
            return MaxValue;
        }

        if (exponentRaw <= (-18L << FractionBitCount)) {
            return Zero;
        }

        return Exp2(value: new(Value: (long)exponentRaw));
    }
    /// <summary>Linearly interpolates from <paramref name="from"/> to <paramref name="to"/> by <paramref name="amount"/>.</summary>
    /// <param name="from">The value returned when <paramref name="amount"/> is zero.</param>
    /// <param name="to">The value returned when <paramref name="amount"/> is one.</param>
    /// <param name="amount">The interpolation fraction; values outside <c>[0, 1]</c> extrapolate.</param>
    /// <returns><c><paramref name="from"/> + (<paramref name="to"/> − <paramref name="from"/>)·<paramref name="amount"/></c> — exactly <paramref name="from"/> at zero and <paramref name="to"/> at one, wrapping on overflow like the operators.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ4816 Lerp(FixedQ4816 from, FixedQ4816 to, FixedQ4816 amount) =>
        (from + ((to - from) * amount));
    /// <summary>Returns the greater of two values.</summary>
    /// <param name="x">The first value to compare.</param>
    /// <param name="y">The second value to compare.</param>
    /// <returns>Whichever of <paramref name="x"/> and <paramref name="y"/> is greater.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ4816 Max(FixedQ4816 x, FixedQ4816 y) =>
        new(Value: Math.Max(
        val1: x.Value,
        val2: y.Value
    ));
    /// <summary>Returns the lesser of two values.</summary>
    /// <param name="x">The first value to compare.</param>
    /// <param name="y">The second value to compare.</param>
    /// <returns>Whichever of <paramref name="x"/> and <paramref name="y"/> is lesser.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ4816 Min(FixedQ4816 x, FixedQ4816 y) =>
        new(Value: Math.Min(
        val1: x.Value,
        val2: y.Value
    ));
    /// <summary>Rounds <paramref name="value"/> to the nearest integral value, with ties rounded to the nearest even integer.</summary>
    /// <param name="value">The value to round.</param>
    /// <returns><paramref name="value"/> rounded to a whole number using banker's rounding.</returns>
    /// <exception cref="OverflowException">The rounded result exceeds <see cref="MaxValue"/>.</exception>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ4816 Round(FixedQ4816 value) {
        // Floor + round the [0,1) fraction (ties to even); for two's-complement the low 16 bits are the fraction
        // above the floor for both signs, so a single path handles negatives correctly.
        var integerPart = value.Value & IntegerBitMask;
        var fraction = (ulong)value.Value & FractionBitMask;
        var roundUp = ((fraction > RawHalf) || ((fraction == RawHalf) && (((integerPart >> FractionBitCount) & 1L) != 0L)));

        return new(Value: (roundUp
            ? checked((integerPart + RawOne))
            : integerPart));
    }
    /// <summary>Returns an integer that indicates the sign of <paramref name="value"/>.</summary>
    /// <param name="value">The value whose sign is returned.</param>
    /// <returns><c>-1</c>, <c>0</c>, or <c>1</c> according to whether <paramref name="value"/> is negative, zero, or positive — the sign of the raw storage, which the <c>2¹⁶</c> scale preserves.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static int Sign(FixedQ4816 value) =>
        Math.Sign(value: value.Value);
    /// <summary>Returns the non-negative square root of <paramref name="value"/>.</summary>
    /// <param name="value">The value whose square root is returned; non-positive inputs yield zero.</param>
    /// <returns>The floor of the square root of <paramref name="value"/>, in fixed point.</returns>
    /// <remarks>The result is exactly <c>⌊√(raw · 2¹⁶)⌋</c>. Hardware square roots only seed the estimate; an
    /// integer settle pins the exact floor, so results are bit-identical across machines and to the pure-integer
    /// fallback.</remarks>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ4816 Sqrt(FixedQ4816 value) {
        if (value.Value <= 0L) {
            return Zero;
        }

        // √(raw/2^16)·2^16 = √(raw·2^16). Below 2^48 the scaled value fits 64 bits and takes the hardware-seeded
        // integer square root; wider inputs seed from a double square root and settle to the exact integer floor.
        if (value.Value < (1L << IntegerBitCount)) {
            return new(Value: unchecked((long)(((ulong)value.Value) << FractionBitCount).SquareRoot()));
        }

        var scaled = (((UInt128)(ulong)value.Value) << FractionBitCount);
        var root = ((UInt128)(ulong)Math.Sqrt(d: ((double)scaled)));

        while ((root * root) > scaled) { --root; }
        while (((root + UInt128.One) * (root + UInt128.One)) <= scaled) { ++root; }

        return new(Value: ((long)root));
    }
    /// <summary>Computes the angle, in radians, from the positive X axis to the point <c>(<paramref name="x"/>, <paramref name="y"/>)</c>.</summary>
    /// <param name="y">The ordinate (the Y component).</param>
    /// <param name="x">The abscissa (the X component).</param>
    /// <returns>The angle in <c>(−π, π]</c>, in fixed-point radians; zero when both arguments are zero.</returns>
    /// <remarks>Pure integer arithmetic (one hardware ratio division, then a per-interval cubic at Q61);
    /// bit-identical across machines. Maximum observed error is 0.51 ULP.</remarks>
    public static FixedQ4816 Atan2(FixedQ4816 y, FixedQ4816 x) {
        if (
            (x.Value == 0L) &&
            (y.Value == 0L)
        ) {
            return Zero;
        }

        // Octant fold: z = min/max ∈ [0, 1] at Q62 via one 128-by-64 division (the quotient always fits — the
        // dividend's high word is min >> 2, below the divisor).
        var signY = (y.Value >> 63);
        var signX = (x.Value >> 63);
        var yMagnitude = unchecked((ulong)((y.Value ^ signY) - signY));
        var xMagnitude = unchecked((ulong)((x.Value ^ signX) - signX));
        var swapped = (yMagnitude > xMagnitude);
        var numerator = (swapped
            ? xMagnitude
            : yMagnitude);
        var denominator = (swapped
            ? yMagnitude
            : xMagnitude);
        ulong z;

        if (X86Base.X64.IsSupported) {
#pragma warning disable SYSLIB5004
            (z, _) = X86Base.X64.DivRem(
                lower: (numerator << 62),
                upper: (numerator >> 2),
                divisor: denominator
            );
#pragma warning restore SYSLIB5004
        } else {
            z = ((ulong)((((UInt128)numerator) << 62) / denominator));
        }

        // Per-interval cubic in h = z − z_i (exact; no second division). z == 2^62 (equal magnitudes) shares the
        // top interval with h = 2^-7.
        var index = ((int)(z >> 55));

        if (index > 127) {
            index = 127;
        }

        var h = ((long)(z - (((ulong)index) << 55)));
        var acc = AtanDerivative3TableQ61[index];

        acc = (AtanDerivative2TableQ61[index] + BigMulShift62(
            x: h,
            y: acc
        ));
        acc = (AtanDerivative1TableQ61[index] + BigMulShift62(
            x: h,
            y: acc
        ));

        var angle = (AtanTableQ61[index] + BigMulShift62(
            x: h,
            y: acc
        ));

        if (swapped) {
            angle = (Atan2HalfPiQ61 - angle);
        }

        if (signX != 0L) {
            angle = (Atan2PiQ61 - angle);
        }

        var raw = ((angle + (1L << 44)) >> 45);

        return new(Value: ((signY != 0L)
            ? -raw
            : raw));
    }
    /// <summary>Computes the cosine of <paramref name="angle"/>, given in fixed-point radians.</summary>
    /// <param name="angle">The angle in radians.</param>
    /// <returns>The cosine, in <c>[−1, 1]</c>. Prefer <see cref="SinCos"/> when both the sine and cosine are needed.</returns>
    public static FixedQ4816 Cos(FixedQ4816 angle) =>
        SinCos(angle: angle).Cos;
    /// <summary>Computes the sine of <paramref name="angle"/>, given in fixed-point radians.</summary>
    /// <param name="angle">The angle in radians.</param>
    /// <returns>The sine, in <c>[−1, 1]</c>. Prefer <see cref="SinCos"/> when both the sine and cosine are needed.</returns>
    public static FixedQ4816 Sin(FixedQ4816 angle) =>
        SinCos(angle: angle).Sin;
    /// <summary>Computes the sine and cosine of <paramref name="angle"/> (in fixed-point radians) in a single pass.</summary>
    /// <param name="angle">The angle in radians; any representable value is accepted — reduction is exact in the turn domain.</param>
    /// <returns>The pair <c>(Sin, Cos)</c>, each in <c>[−1, 1]</c>.</returns>
    /// <remarks>Pure integer arithmetic (turn-domain reduction, then odd/even polynomials at Q60); bit-identical
    /// across machines. <c>SinCos(Atan2(y, x))</c> recovers the unit direction. Maximum observed error is 0.51 ULP
    /// within a few turns of zero and ~2 ULP at extreme magnitudes.</remarks>
    public static (FixedQ4816 Sin, FixedQ4816 Cos) SinCos(FixedQ4816 angle) {
        // Reduce in turns: raw · round(2^64/2π) = turns · 2^80; the two's-complement wrap of the 128-bit product is
        // the exact mod-one-turn reduction.
        var high = Math.BigMul(
            a: angle.Value,
            b: SinCosInvTwoPiQ64,
            low: out var low
        );

        return SinCosFromTurns(fractionalTurns: unchecked((long)((((ulong)low) >> FractionBitCount) | (((ulong)high) << IntegerBitCount))));
    }

    // Full-range norm overload: phases a non-negative raw Q16 magnitude that may exceed the signed carrier (a
    // three-component norm always roots within 64 unsigned bits). Same turn-domain wrap as SinCos.
    internal static (FixedQ4816 Sin, FixedQ4816 Cos) SinCosRaw(ulong rawAngle) {
        var product = ((UInt128)rawAngle * ((ulong)SinCosInvTwoPiQ64));

        return SinCosFromTurns(fractionalTurns: unchecked((long)((ulong)(product >> FractionBitCount))));
    }

    private static (FixedQ4816 Sin, FixedQ4816 Cos) SinCosFromTurns(long fractionalTurns) {
        var (cosQ60, sinQ60, folded) = SinCosCore(fractionalTurns: fractionalTurns);

        // Q60 → Q16: round to nearest (ties toward +∞), clamp to ±1.
        const int narrowingShift = (SinCosFractionBitCount - FractionBitCount);
        var sinRaw = Math.Clamp(
            value: ((sinQ60 + (1L << (narrowingShift - 1))) >> narrowingShift),
            min: -RawOne,
            max: RawOne
        );
        var cosRaw = Math.Clamp(
            value: ((cosQ60 + (1L << (narrowingShift - 1))) >> narrowingShift),
            min: -RawOne,
            max: RawOne
        );

        return (new(Value: sinRaw), new(Value: (folded
            ? -cosRaw
            : cosRaw)));
    }

    // Rounds a wide product (a raw Q16 factor times a 2^fractionBitCount-scaled ratio) to raw Q16, ties to even.
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    internal static long RoundProduct(Int128 product, int fractionBitCount) {
        var sign = (product >> 127);
        var magnitude = unchecked((UInt128)((product ^ sign) - sign));
        var truncated = ((ulong)(magnitude >> fractionBitCount));
        var remainder = (magnitude & ((UInt128.One << fractionBitCount) - UInt128.One));
        var half = (UInt128.One << (fractionBitCount - 1));

        if ((remainder > half) || ((remainder == half) && ((truncated & 1UL) != 0UL))) {
            ++truncated;
        }

        var result = unchecked((long)truncated);
        var resultSign = unchecked((long)sign);

        return unchecked(((result ^ resultSign) - resultSign));
    }

    // Fast rotation-scale overload. Callers are responsible for keeping the raw Q32 sum in signed 64-bit range.
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    internal static long RoundProductSum(long productSum) {
        var sign = (productSum >> 63);
        var magnitude = unchecked((ulong)((productSum ^ sign) - sign));
        var truncated = (magnitude >> FractionBitCount);
        var remainder = magnitude & FractionBitMask;

        if (
            (remainder > RawHalf) ||
            ((remainder == RawHalf) && ((truncated & 1UL) != 0UL))
        ) {
            ++truncated;
        }

        var result = unchecked((long)truncated);

        return unchecked(((result ^ sign) - sign));
    }

    // Full-width overload. Rounds a sum of raw Q32 products to raw Q16, once, to nearest with ties to even. Callers
    // widen EACH product to Int128 before accumulating. Unchecked Int128 accumulation is sufficient when an exact sum exceeds
    // 128 bits: wrapping changes the Q32 sum by k·2^128, hence the rounded Q16 result by k·2^112, which vanishes under
    // the public raw operators' final 64-bit wrapping policy without changing tie parity.
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    internal static long RoundProductSum(Int128 productSum) {
        var sign = (productSum >> 127);
        var magnitude = unchecked((UInt128)((productSum ^ sign) - sign));
        var truncated = (magnitude >> FractionBitCount);
        var remainder = ((ulong)magnitude & FractionBitMask);

        if (
            (remainder > RawHalf) ||
            ((remainder == RawHalf) && ((truncated & UInt128.One) != UInt128.Zero))
        ) {
            ++truncated;
        }

        var result = unchecked((long)truncated);

        var resultSign = unchecked((long)sign);

        return unchecked(((result ^ resultSign) - resultSign));
    }

    // Signed (x·y) >> 62 via one 64×64→128 multiply; |x·y| must stay below 2^125.
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    private static long BigMulShift62(long x, long y) {
        var high = Math.BigMul(
            a: x,
            b: y,
            low: out var low
        );

        return (high << 2) | ((long)(((ulong)low) >> 62));
    }
    // Signed (x·y) >> 60 via one 64×64→128 multiply; |x·y| must stay below 2^123.
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    private static long BigMulShift60(long x, long y) {
        var high = Math.BigMul(
            a: x,
            b: y,
            low: out var low
        );

        return (high << 4) | ((long)(((ulong)low) >> 60));
    }

    // Fractional base-2 log of a Q62 mantissa in [1, 2), at Q61: 128-interval inverse table plus a quartic in the
    // residual r = m/d_i − 1 ≤ 2^-7 (total error ≤ ~2^-36; inverse rounding can leave r, and therefore the result,
    // a few raw units negative). Shared by Log2 and the Gaussian sampler.
    internal static long Log2FractionQ61(ulong mantissaQ62) {
        var index = ((int)((mantissaQ62 >> 55) & 0x7FUL));
        var high = Math.BigMul(
            a: mantissaQ62,
            b: Log2InverseTableQ62[index],
            low: out var low
        );
        var r = unchecked((long)(((high << 2) | (low >> 62)) - (1UL << 62)));
        var acc = Log2PolyC4Q61;

        acc = (Log2PolyC3Q61 + BigMulShift62(
            x: r,
            y: acc
        ));
        acc = (Log2PolyC2Q61 + BigMulShift62(
            x: r,
            y: acc
        ));
        acc = (Log2PolyC1Q61 + BigMulShift62(
            x: r,
            y: acc
        ));

        return (((long)Log2TableQ61[index]) + BigMulShift62(
            x: r,
            y: acc
        ));
    }

    // Polynomial core on fractional turns (2^64 raw = one turn). Returns the un-narrowed Q60 cosine/sine of the
    // folded residual plus the fold flag; the true cosine is negated when folded. Internal so the Gaussian sampler
    // can feed full-resolution turns (2^-32 granularity) without the radian round-trip.
    internal static (long CosQ60, long SinQ60, bool Folded) SinCosCore(long fractionalTurns) {
        var folded = ((fractionalTurns > SinCosQuarterTurnQ64) || (fractionalTurns < -SinCosQuarterTurnQ64));

        if (folded) {
            // sin(π − θ) = sin θ and cos(π − θ) = −cos θ: half a turn minus the fraction wraps into [−¼, ¼].
            fractionalTurns = unchecked((long)(0x8000000000000000UL - ((ulong)fractionalTurns)));
        }

        // Radians at Q60 (the fold bounds |θ| ≤ π/2), then Horner on u = θ².
        var x = Math.BigMul(
            a: fractionalTurns,
            b: SinCosTwoPiQ60,
            low: out _
        );
        var u = BigMulShift60(
            x: x,
            y: x
        );
        var sinAcc = SinPolyC6Q60;

        sinAcc = (SinPolyC5Q60 + BigMulShift60(
            x: u,
            y: sinAcc
        ));
        sinAcc = (SinPolyC4Q60 + BigMulShift60(
            x: u,
            y: sinAcc
        ));
        sinAcc = (SinPolyC3Q60 + BigMulShift60(
            x: u,
            y: sinAcc
        ));
        sinAcc = (SinPolyC2Q60 + BigMulShift60(
            x: u,
            y: sinAcc
        ));
        sinAcc = (SinPolyC1Q60 + BigMulShift60(
            x: u,
            y: sinAcc
        ));
        sinAcc = (SinPolyC0Q60 + BigMulShift60(
            x: u,
            y: sinAcc
        ));

        var cosAcc = CosPolyC7Q60;

        cosAcc = (CosPolyC6Q60 + BigMulShift60(
            x: u,
            y: cosAcc
        ));
        cosAcc = (CosPolyC5Q60 + BigMulShift60(
            x: u,
            y: cosAcc
        ));
        cosAcc = (CosPolyC4Q60 + BigMulShift60(
            x: u,
            y: cosAcc
        ));
        cosAcc = (CosPolyC3Q60 + BigMulShift60(
            x: u,
            y: cosAcc
        ));
        cosAcc = (CosPolyC2Q60 + BigMulShift60(
            x: u,
            y: cosAcc
        ));
        cosAcc = (CosPolyC1Q60 + BigMulShift60(
            x: u,
            y: cosAcc
        ));
        cosAcc = (CosPolyC0Q60 + BigMulShift60(
            x: u,
            y: cosAcc
        ));

        return (cosAcc, BigMulShift60(
            x: x,
            y: sinAcc
        ), folded);
    }
    /// <summary>Returns the integral part of <paramref name="value"/>, discarding the fraction (rounding toward zero).</summary>
    /// <param name="value">The value to truncate.</param>
    /// <returns><paramref name="value"/> with its fractional part removed toward zero.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static FixedQ4816 Truncate(FixedQ4816 value) {
        var floor = value.Value & IntegerBitMask;

        // Floor rounds toward −∞; for a negative value with a fraction, truncation toward zero is one step higher.
        return new(Value: (((value.Value < 0L) && ((value.Value & (long)FractionBitMask) != 0L))
            ? unchecked((floor + RawOne))
            : floor));
    }
    /// <summary>Compares this instance with a boxed <see cref="FixedQ4816"/> and indicates their relative order.</summary>
    /// <param name="obj">The object to compare with this instance, or <see langword="null"/>.</param>
    /// <returns>A negative value, zero, or a positive value according to whether this instance precedes, equals, or follows <paramref name="obj"/>; a <see langword="null"/> <paramref name="obj"/> sorts first.</returns>
    /// <exception cref="ArgumentException"><paramref name="obj"/> is neither <see langword="null"/> nor a <see cref="FixedQ4816"/>.</exception>
    public int CompareTo(object? obj) {
        if (obj is null) { return 1; }
        if (obj is FixedQ4816 other) { return CompareTo(other: other); }

        throw new ArgumentException(
            message: $"Object must be of type {nameof(FixedQ4816)}.",
            paramName: nameof(obj)
        );
    }
    /// <summary>Compares this instance with another <see cref="FixedQ4816"/> and indicates their relative order.</summary>
    /// <param name="other">The value to compare with this instance.</param>
    /// <returns>A negative value, zero, or a positive value according to whether this instance precedes, equals, or follows <paramref name="other"/>.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public int CompareTo(FixedQ4816 other) =>
        Value.CompareTo(value: other.Value);
    /// <summary>Returns the exact decimal string representation of this value.</summary>
    /// <returns>The exact, invariant-culture decimal expansion of this value (a <c>/2¹⁶</c> fraction always terminates within sixteen digits).</returns>
    public override string ToString() {
        Span<char> buffer = stackalloc char[MaximumFormattedLength];

        _ = TryFormatCore(destination: buffer, charsWritten: out var charsWritten);

        return new string(value: buffer[..charsWritten]);
    }
}
