# What open 3D libraries learned

Lane lib-1 of the creation-library research round. The owner's ruling for the round: "Research,
no copying. This is inspirational, and Puck is VERY different from what the state of the art might
normally describe or rely on." This page borrows lessons, never content, code or formats. Its
companions in the round are [authoring shapes as programs](programs.md) and lane lib-3's
`puck-creations.md` (Puck's creations as they are).

Labels: **[opened]** means the page or file was opened and the quoted words were pasted from it;
**[summary]** means a search result that was not opened; **[inference]** means reasoning from what
was opened. Some quotes came back through a fetch tool that limits each quote's length. Where that
tool paraphrased instead of quoting, the claim is marked "(fetch paraphrase)".

## Summary

- **Google Poly** was a free, host-run library fed by Google's own creation tools (Tilt Brush and
  the low-poly Blocks). A remixable asset was CC-BY, credit was written by the platform, and a
  read API let any app search by category, curation and triangle-count class. Google closed it in
  2021 to put its resources into AR. Only content that fans had salvaged survived: what the API had
  exposed under a licence that allows copying.
- **Amazon Berkeley Objects (ABO)** is a research dataset of 7,953 artist-made glTF models of real
  Amazon products, each with PBR material maps, a fixed placement convention and real-world units,
  joined to the catalog listing's dimensions, weight and material. A bounded check run for this page
  found the models' extents agree with the listings' stated dimensions: the median ratio of largest
  extents is 1.000, and 59% of models match on all three axes within 10%.
- **The contrasts** repeat the same few lessons. Consistency comes from one hand or a constrained
  tool (Kenney, Quaternius, Blocks), not from moderation (Sketchfab and Objaverse needed later
  filtering). Scale holds where one door enforces a convention (ABO, Unity's submission review,
  Voyager's required `units`) and drifts where none does (Sketchfab, as Objaverse found).
  Licences fail where they are a label beside the bytes rather than part of them: Thingiverse's 2012
  terms, 3DBenchy's late-enforced BY-ND, Printables' remix-licence conflicts, Quaternius's
  FAQ-versus-licence disagreement, Sketchfab's NoAI tag that cannot follow CC content.
- **For Puck**, which already pins creations by content hash and treats derived products as caches,
  the lessons below mostly turn into properties of the canonical creation document and checks at
  compile time, not into a hosted service.

## Google Poly

### What it offered

- **[opened]** Google's launch post called Poly "one place to browse, discover and download 3D
  objects", "built from the ground up with AR and VR development in mind", and said "It's fully
  integrated with Tilt Brush and Blocks,"
  ([blog.google, Poly launch](https://blog.google/products/google-VR/poly-browse-discover-and-download-3d-objects-and-scenes/)).
- **[opened]** Blocks was "an intuitive free tool for creating low-poly 3D models in virtual reality."
  ([CG Channel on the Blocks source release](https://www.cgchannel.com/2024/08/google-open-sources-virtual-reality-modeling-app-blocks)).
- **[opened]** Wikipedia: "On November 30, 2017, Google released the Poly API.", and "the Poly Toolkit
  was required for Unity and Unreal developers"
  ([Wikipedia, Poly (website)](https://en.wikipedia.org/wiki/Poly_(website))).
- **[inference]** Most of Poly's look came from its feeder tools: Blocks only made low-poly models,
  so a large share of the library shared one faceted style without any style review. This is the
  "constrained tool" route to consistency that recurs below.

### Formats and licences

- **[opened]** Uploads were converted: "Assets you upload to Poly in the above formats get converted
  to glTF and USDZ formats." The same page says "Poly currently only supports the Wavefront .obj file
  format." and "The maximum upload size per asset is 100MB"
  ([Poly help, Supported formats](https://support.google.com/poly/answer/7562662)).
- **Sources disagree.** **[opened]** A second help page lists ".obj, .glTF, .glb" (fetch
  paraphrase of the list after "You can upload content to Poly via poly.google.com in any of these
  file formats:") and "You can also upload to Poly directly through the Tilt Brush and Blocks apps."
  ([Poly help, Uploading to Poly](https://support.google.com/poly/answer/9470475?hl=en)).
  **[inference]** The two pages date from different stages of the service; neither carries a date.
- **[opened]** Licence was tied to remixability: "If you allow remixing, your content will be
  published under a CC-BY license." A remix took a copy: "This allows others to use a copy of your
  work in a new creation." and "You won't be able to edit or delete this copy." Credit was the
  platform's job: "Your attribution will automatically be shown on any remixed content published to
  Poly." ([Uploading to Poly](https://support.google.com/poly/answer/9470475?hl=en)).
- **[opened]** "Objects that are published as re-mixable will also be downloadable by users."
  ([Supported formats](https://support.google.com/poly/answer/7562662)). **[inference]** So
  "downloadable", "remixable" and "CC-BY" were one switch, which kept the licence story simple for
  users: an asset was either open or it was not.
- **[opened]** Visibility had a middle state: "Unlisted content isn't searchable or featured on the
  homepage, and it won't be shown on your profile."
  ([Uploading to Poly](https://support.google.com/poly/answer/9470475?hl=en)).

### The API

- **[opened]** Reference docs for the Poly API: "The Poly API provides read access to assets hosted on
  poly.google.com to all," "and upload access to poly.google.com for whitelisted accounts."
  `assets.list` "Lists all public, remixable assets." "These are assets with an access level of
  PUBLIC and published under the CC-By license." Filters: `curated` ("Return only assets that have
  been curated by the Poly team."), `maxComplexity` ("Returns assets that are of the specified
  complexity or less. Defaults to COMPLEX."), `format` ("`BLOCKS`, `FBX`, `GLTF`, `GLTF2`, `OBJ`,
  `TILT`"), and `orderBy` ("Defaults to `BEST`, which ranks assets based on a combination of
  popularity and other features.")
  ([googleapis.dev, Poly class](https://googleapis.dev/nodejs/googleapis/latest/poly/classes/Poly.html)).
- **[opened]** Each asset carried credit and lineage: `authorName` is "The author's publicly visible
  name. Use this name when giving credit to the author."
  ([Schema$Asset](https://googleapis.dev/nodejs/googleapis/latest/poly/interfaces/Schema$Asset.html));
  `RemixInfo` is "Info about the sources of this asset (i.e. assets that were remixed to create this
  asset)."
  ([PolyService Data](https://googleapis.dev/dotnet/Google.Apis.PolyService.v1/latest/api/Google.Apis.PolyService.v1.Data.html)).
- **[opened]** Each format carried a cost hint: `triangleCount` is "The estimated number of
  triangles." and `lodHint` "allows you to sort formats from the most-detailed (0) to least-detailed
  (integers greater than 0)."
  ([Schema$FormatComplexity](https://googleapis.dev/nodejs/googleapis/latest/poly/interfaces/Schema$FormatComplexity.html)).
- **[opened]** Orientation was a mutable hint, and there was no scale field: `orientingRotation` is "A
  rotation that should be applied to the object root to make it upright." with "Please note: this is
  applicable only to the gLTF."
  ([Schema$PresentationParams](https://googleapis.dev/nodejs/googleapis/latest/poly/interfaces/Schema$PresentationParams.html)),
  and presentation parameters "are not immutable; the author of an asset may change them
  post-publication." ([Schema$Asset](https://googleapis.dev/nodejs/googleapis/latest/poly/interfaces/Schema$Asset.html)).
  **[inference]** No unit or scale member appears among the asset, format or presentation fields
  opened, so consumers had to guess the size of every model.

### Why it closed

- **[opened]** Google's help centre: "Poly was shut down on June 30, 2021." "The ability to upload new
  3D models on poly.google.com was disabled on April 30, 2021." "The ability to download assets was
  disabled on June 30, 2021." It gives no reason
  ([Poly help, shutdown](https://support.google.com/poly/answer/10192635)).
- **[opened]** The user email, as quoted by 9to5Google, said that after the June 2021 shutdown
  "poly.google.com and associated APIs will no longer be accessible."
  ([9to5Google](https://9to5google.com/2020/12/02/google-poly-shutdown/)).
- **[opened, reprint]** The stated reason, given to BleepingComputer and quoted in a reprint: "We're
  shifting resources so we can continue investing heavily in helpful AR experiences"
  ([privacy.com.sg reprint](https://www.privacy.com.sg/?p=10699)). The BleepingComputer original was
  not reached.
- **[opened]** The feeder tools ended the same way. Tilt Brush's source was released with "there were
  a few things we had to change or remove due to licensing restrictions." and "no pull requests will
  be accepted." ([Google Open Source blog](https://opensource.googleblog.com/2021/01/the-future-of-tilt-brush.html));
  Blocks' source followed, "available under an Apache 2.0 licence."
  ([CG Channel](https://www.cgchannel.com/2024/08/google-open-sources-virtual-reality-modeling-app-blocks)).

### What happened to its content

- **[opened]** Google pointed users elsewhere: "Other partners, such as Sketchfab, support hosting
  models previously uploaded to Poly." and "Expeditions and Tours will not be supported by
  Sketchfab." ([Poly help, shutdown](https://support.google.com/poly/answer/10192635)).
- **[opened]** Archive Team lists Poly as "Saved!", notes "The Poly API requires an API key.", and
  names an Internet Archive item `archiveteam_googlepoly` whose access is restricted (fetch
  paraphrase) ([Archive Team wiki](https://wiki.archiveteam.org/index.php/Google_Poly)).
- **[opened]** A fan archive describes itself as "an archive of free 3D assets salvaged from Google
  Poly", says "This content is published under a CC-BY License." and "You can download the archive
  using torrent." ([polygone.art repository](https://github.com/arodic/polygone.art)).
- **[opened]** Icosa Gallery is "a community-built, open-source replacement for Google Poly", and users
  could "import all of your current Poly creations into Icosa Gallery"
  ([UploadVR](https://www.uploadvr.com/icosa-gallery-beta)); its own page says it lets you "host
  your own instance publicly or privately on premise." ([Icosa about](https://icosa.gallery/about)).
  Its maintainer, offering the data to Objaverse: "I'm working on an open source replacement for
  Google Poly" and "Internet Archive has all the data." (about 130,000 models, fetch paraphrase)
  ([Hugging Face discussion](https://huggingface.co/datasets/allenai/objaverse/discussions/59)).
- **[summary]** Poly Pizza, another successor, reports thousands of CC0 and CC-BY low-poly models and
  an API mostly compatible with Poly's.
- **[inference]** What survived was exactly what was public, CC-BY and reachable through an API that
  handed out file URLs: the licence allowed copying and the API made copying mechanical. Private,
  unlisted and all-rights-reserved work, the Tilt Brush and Blocks remix lineage inside Google's
  service, and the curation flags did not survive as a working library, although `RemixInfo` is
  preserved where the archive kept the API's metadata.

## Amazon Berkeley Objects

### What it is and how it was made

- **[opened]** The dataset page: "A CC BY 4.0-licensed dataset of Amazon products with metadata,
  catalog images, and 3D models." and "The dataset contains high-quality 3D models with 4K texture
  maps for physically based rendering for more than 7'900 products. The models are provided in the
  standard glTF 2.0 format." Materials are "texture maps of SVBRDF properties: base color, metallic
  and roughness." ([ABO dataset page](https://amazon-berkeley-objects.s3.amazonaws.com/index.html)).
- **[opened]** The paper (line breaks joined): "ABO contains 147,702 product listings associated with
  398,212 unique catalog images, and up to 18 unique metadata attributes (category, color, material,
  weight, dimensions, etc.) per product." and "ABO also includes 7, 953 artist-created high-quality 3D
  models in glTF 2.0 format." The listings come from "various Amazon-owned stores and websites (e.g.
  Amazon, PrimeNow, Whole Foods)."
  ([Collins et al., ABO, arXiv 2110.06199v2](https://arxiv.org/abs/2110.06199)).
- **[opened]** Its purpose was research: the paper benchmarks "(i) single-view shape reconstruction,
  where we measure the domain gap for networks trained on synthetic objects, (ii) material
  estimation, ... and (iii) image-based multi-view object retrieval", and closes with "the 3D objects
  in ABO correspond to items that naturally occur in a home, and have associated object weight and
  dimensions. This can benefit robotics research and support simulations of manipulation and
  navigation." (same paper).
- **[inference]** Neither the paper nor the dataset page says why Amazon had artist-made models of
  these products or who made them, beyond "artist-created". Product visualisation in Amazon's
  storefront is a plausible origin, unverified.

### True scale and placement

- **[opened]** The paper: "The 3D models are oriented in a canonical coordinate system where the
  "front" (when well defined) of all objects are aligned and each have a scale corresponding to real
  world units." (same paper).
- **[opened]** The 3D models README spells the convention out: "Positive `Y` direction is up.",
  "Positive `Z` direction is pointing to the *natural* front-side of the product, wherever
  applicable.", products designed to stand "are centered on the origin, but translated up (towards
  positive `Y`) such that they *stand* on the `Y=0` plane.", hanging products "*hang* from the `Y=0`
  plane.", and wall products are "translated forward (towards positive `Z`) such that their backside
  aligns with the `Z=0` plane."
  ([ABO 3dmodels README](https://amazon-berkeley-objects.s3.amazonaws.com/3dmodels/README.md)).
- **[opened]** The model metadata carries `extent_x`, `extent_y`, `extent_z` ("Extent of the geometry
  in each dimension"), plus `vertices` and `faces` per model (same README); listings carry
  `item_dimensions` (height, length, width) and `item_weight` with units, and `color_code` hex
  values ([dataset page](https://amazon-berkeley-objects.s3.amazonaws.com/index.html)).
- **Measured for this page.** A bounded check read the public `3dmodels/metadata/3dmodels.csv.gz` and
  all sixteen `listings/metadata/listings_*.json.gz` files, took each model's three extents as
  metres, converted the listing's normalized `item_dimensions` to metres, and compared the two sorted
  triples (sorting removes the length/width/depth naming question). What it showed:
  - 147,702 listings, of which 7,960 name a `3dmodel_id`; 7,953 models in the metadata; every
    dimension unit seen on those listings was `inches`.
  - 7,785 models had complete listing dimensions. All three sorted extents fell within 10% of the
    listing for 4,605 of them (59.2%), and within 25% for 5,658 (72.7%).
  - The ratio of largest model extent to largest listing dimension has median 1.000, 10th percentile
    0.972 and 90th percentile 1.147; only 4 models sit near a unit-conversion factor (about x25, x40,
    x100 or their inverses).
  - Face counts per model: median 18,240, minimum 20, maximum 11,540,224.
  - The dataset page's example sofa, `B075X4QMX3` (listed as 87 inches wide, 37.75 deep and 33
    high), has sorted extents of 0.90, 0.91 and 2.20 m against the listing's 0.84, 0.96 and 2.21 m.

  What it decided: ABO's "real world units" claim holds as metres for the bulk of the set, and the
  disagreements are mostly modest on one axis, not unit errors. **[inference]** A listing's
  dimensions are retail data (a sofa's "D" may include cushions or exclude legs), so the 27% beyond
  25% are as likely listing quirks as model errors; nothing here can tell which.
- **[inference]** Two independent fields (the model's geometry and the listing's numbers) agreeing is
  what makes "true scale" checkable at all. A library with only the geometry can claim scale but
  cannot show it.

### Licence and intended use

- **[opened]** The dataset page and README: "This work is licensed under the Creative Commons
  Attribution 4.0 International Public License (CC BY 4.0)." Credit "for the data, including all
  images and 3D models, must be given to: Amazon.com", and the README warns of image URLs: "there is
  no guarantee that these URLs will remain unchanged and available on the long term"
  ([ABO README](https://amazon-berkeley-objects.s3.amazonaws.com/README.md)).
- **Sources disagree.** **[opened]** The arXiv v2 paper says: "The dataset is released under CC BY-NC
  4.0 license" ([arXiv 2110.06199v2](https://arxiv.org/abs/2110.06199)). The live dataset page and
  README say CC BY 4.0. **[inference]** The licence was relaxed after the paper's text was fixed; a
  reader who trusts the paper gets the wrong terms.
- **[inference]** The models are branded retail products ("Stone & Beam Westport Modern Nailhead
  Upholstered Sofa" in the page's own example). A copyright licence does not clear trademarks or
  product designs; the Smithsonian states the same limit for its CC0 releases (below).

## Contrasts

### Sketchfab

- **What made it useful.** **[summary]** A web viewer for any uploaded model, downloads under a
  per-model licence, and the cultural-heritage collections that museums host there.
- **Licensing and remix.** **[summary]** Free downloads carry a Creative Commons licence or Sketchfab's
  Standard licence; paid Store models used Standard or Editorial. **[opened]** The NoAI tag
  "indicates to generative AI programs that a particular model is not to be used for generative AI
  data collection." For CC content it "may not be enforceable outside of the Sketchfab platform",
  and "Starting today, you will no longer be able to apply the NoAI tag to new uploads of
  CC-licensed content." Sketchfab's reason: "uncertainties in copyright law make it difficult for
  both Sketchfab and creators to enforce NoAI restrictions"
  ([Sketchfab blog, Restricting generative AI use](https://sketchfab.com/blogs/community/restricting-generative-ai-use-of-free-models)).
- **Quality and scale.** **[opened]** Objaverse, built from Sketchfab: "Objects in Sketchfab may be
  uploaded at unnatural scales", so "we scale the objects to be of a reasonable size for them to look
  natural in a house" ([Objaverse paper](https://ar5iv.labs.arxiv.org/html/2212.08051)).
- **What failed.** **[opened]** "On July 21, 2021, Epic Games acquired Sketchfab."
  ([Wikipedia, Sketchfab](https://en.wikipedia.org/wiki/Sketchfab)). Epic's stated aim for Fab was to
  "give all digital content creators a single destination to discover, share, buy and sell digital
  assets." ([Announcing Fab](https://sketchfab.com/blogs/community/announcing-fab-the-next-phase-for-sketchfab)).
  "The Sketchfab Store is now closed." "Free and view-only models will remain where they are." "We
  will provide plenty of notice before free downloadable content is no longer supported on
  Sketchfab." ([Sketchfab update after Fab](https://sketchfab.com/blogs/community/sketchfab-update-what-you-need-to-know-now-that-fabs-live/)).
  The migration announcement said non-BY CC content "cannot migrate to Fab under those license
  types." because "those licenses do not exist on Fab at this time." A petition answered "This is the
  virtual equivalent of burning the Library of Alexandria."; Epic answered "We have no intention of
  burning any libraries.", "Their models will still be available on Sketchfab for discovery and
  download.", and "We will not remove the ability to share or download content on Sketchfab until we
  have a viable alternative." ([80.lv](https://80.lv/articles/historians-are-concerned-about-epic-games-sketchfab-to-fab-migration)).
  **Unconfirmed:** whether free downloads still work today; no source after early 2025 was found.
- **What the operator said about Objaverse.** **[opened, secondary]** Sketchfab's CEO, as reported:
  the models were "mass aggregated by objaverse without [their] knowledge", with "absolutely zero
  upside in something like this happening", and the dataset "was created before Sketchfab
  implemented the NoAI tag" ([3DVF](https://3dvf.com/en/ai-this-massive-dataset-of-3d-objects-is-a-game-change-heres-why-it-may-include-some-of-your-3d-assets/)).
  **Sources disagree** on who said the last part: a search summary attributes it to the Objaverse
  team.

### Kenney

- **Licensing.** **[opened]** "all game assets on the asset pages are public domain licensed (CC0)";
  "Attribution is not required, but if you choose to give credit you can do so by mentioning
  "Kenney"."; and "Do not use our logo, as it is reserved for official projects by our studio."
  ([Kenney support](https://kenney.nl/support)). A kit page lists "Creative Commons CC0" and "40×"
  files ([Modular Space Kit](https://kenney.nl/assets/modular-space-kit)).
- **Consistency.** **[opened]** Kenney, interviewed: "It's essential that game assets fit together,
  either by slightly changing the style, textures, or colors", and "I personally use programs like MS
  Paint (pixel art), SketchUp (3D modeling), and Adobe Animate (vector art)."
  ([Buildbox interview](https://buildbox.com/buildbox-creator-spotlight-kenney-vleugels)).
- **[inference]** One author, one tool and a deliberate rule that assets fit together is the whole
  quality system; CC0 removes every licence question from the user's side. No page opened states a
  shared grid or unit across kits, so scale consistency across Kenney kits is unverified.

### Quaternius

- **Licensing, and a live disagreement.** **[opened]** The FAQ: "All models are under the CC0
  License.", "attribution is not necessary.", "You can open the .blend files directly or import the
  FBX files.", and "you can request custom assets through my Patreon page."
  ([Quaternius FAQ](https://quaternius.com/faq.html)). **[opened]** The licence page is a different
  licence, "Quaternius Asset License (QAL) v1.0.": you may not "extract, repackage, sublicense, sell,
  or otherwise redistribute the Assets" as standalone assets; "We retain all intellectual property
  rights in the Assets."; "Changes will not apply retroactively to Assets you've already obtained
  under an earlier version."; and "the version in effect at the time you obtained the Assets governs
  your use of them." ([Quaternius licence](https://quaternius.com/license.html)).
- **[inference]** The two pages on one site now disagree, and the QAL's own rule makes the licence a
  property of when a copy was obtained, which nothing in a downloaded `.blend` or FBX records. A
  user who cannot prove the date of acquisition cannot prove which terms apply.

### Thingiverse

- **Licensing.** **[opened]** The 2012 dispute: Prusa objected that the terms meant the site "owns
  everything submitted by Thingiverse users."; the clause granted an "irrevocable, nonexclusive,
  royalty-free and fully paid, worldwide license," "solely for the purposes of including your User
  Content in the Site and Services."
  ([Hackaday, 2012](https://hackaday.com/2012/09/20/makerbot-occupy-thingiverse-and-the-reality-of-selling-open-hardware)).
  **Sources disagree** on whether this was ownership or only a hosting licence (Hackaday's article
  versus the clause text quoted in its comments). **[opened]** MakerBot's later guide: "Attribution
  is a condition of every CC license." ([3DPrint.com](https://3dprint.com/129999/makerbot-proper-attribution/)).
  **[summary]** In 2016 a reseller bulk-copied CC BY-NC designs and MakerBot called it a violation of
  the licences and the terms.
- **Discovery.** **[opened]** Without a single catalogue "it's just not as useful a site as
  Thingiverse."; mirrors were "removing the attribution in violation of these object's licenses."; a
  reader asked for "some kind of a rating system for objects other than 'likes'."
  ([Hackaday, 2013](https://hackaday.com/2013/10/02/3d-printering-the-problem-of-thingiverse)).
- **Parametric remix.** **[opened]** Customizer was "a web app that allows you to create custom
  versions of other people's work right in your browser." that "puts sliders and radio buttons on a
  web page." from an OpenSCAD file's variables
  ([Hackaday, 2013](https://hackaday.com/2013/02/16/custom-3d-printed-designs-with-makerbots-customizer)).
  **[summary]** It later broke and was discontinued; a third party relaunched a replacement.
- **Scale as a dataset.** **[opened]** Objaverse-XL: "We index and download around 3.5 million
  objects from the platform", "predominantly released under Creative Commons licenses"
  ([Objaverse-XL](https://ar5iv.labs.arxiv.org/html/2307.05663)).

### Printables

- **[opened]** In the 3DBenchy episode, Prusa's CMO said Printables acted on a "third-party report.",
  called the removal of remixes "standard procedure", and said the site is "bound to act in
  accordance with the license terms."; NTI's CMO said "NTI has not taken any action to enforce the
  3DBenchy license or taken any steps to have derivatives removed."
  ([3D Printing Industry](https://3dprintingindustry.com/news/3dbenchy-gate-prusa-research-and-nti-group-respond-235583)).
  The model's BY-ND licence "forbids distributing of derivative works." and "is now (belatedly) being
  enforced." ([Hackaday, licensing tag](https://hackaday.com/tag/creative-commons)). It then "has been
  transitioned into the public domain."
  ([3D Printing Industry](https://3dprintingindustry.com/news/3dbenchy-joins-the-public-domain-a-milestone-for-3d-printing-236377/)).
- **[summary]** Printables forum threads report a remix chain mixing share-alike and non-commercial
  branches for which the upload form found that no licence satisfied both, a licence missing from the
  form's list, and slow licence-infringement handling. These are user reports, not policy.
- **[inference]** Printables does compute licence compatibility over a remix tree, which is the right
  idea, but it runs at upload time over self-declared parents, and a ten-year-old BY-ND licence was
  enforced only when someone filed a report.

### Objaverse

- **[opened]** "Objaverse 1.0 includes 818K 3D objects, designed by 160K artists.", "obtained using
  Sketchfab’s public API", and "models marked as restricted due to objectionable or adult thematic
  content were excluded from the dataset."
  ([Objaverse](https://ar5iv.labs.arxiv.org/html/2212.08051)).
- **[opened]** Objaverse-XL deduplicated by bytes, "objects are deduplicated by file content hash,
  which removes approximately 23 million files", conceded "There may still be near duplicates that
  exist", is "distributed under the ODC-By 1.0 license" with objects "subject to the licenses that
  they are released under", and of GitHub files "we were able to import and successfully render 5.5
  million" ([Objaverse-XL](https://ar5iv.labs.arxiv.org/html/2307.05663)).
- **[opened]** A follow-up was written "To address the issue of the prevalence of low-quality models
  in Objaverse", found "about one-fifth of the original Objaverse dataset are scanned objects", and
  that "the higher the data quality, the faster the training loss converges."
  ([Objaverse++](https://arxiv.org/html/2504.07334v2)).
- **[inference]** Aggregation reaches scale quickly and pays for it in licence provenance (a CC label
  is only the uploader's claim), consent (the NoAI dispute above), duplicates that byte hashes cannot
  see, and quality filtering after the fact.

### Smithsonian 3D

- **[opened]** The Open Access FAQ: "We have released these images and data into the public domain as
  Creative Commons Zero (CC0), meaning you can use, transform, and share our open access assets without
  asking permission from the Smithsonian."; formats "3D Images glTF glb obj (150k and full res
  versions) Voyager scenes"; exclusions include "An object is culturally sensitive" and "An object is
  not fully owned by the Smithsonian"; "Open access items designated as CC0 do not require
  attribution or citation, however, it is helpful to provide basic credit information to the
  Smithsonian"; and its data repository says "Please note that the Smithsonian does not support pull
  requests." ([Smithsonian Open Access FAQ](https://www.si.edu/openaccess/faq)).
- **[opened]** Voyager is an "Open source 3D explorer and authoring tool suite."
  ([Voyager](https://smithsonian.github.io/dpo-voyager/)). Its model schema makes units mandatory and
  names derivatives by use and quality:

  ```ts
  export enum EDerivativeUsage { Image2D, Web3D, App3D, iOSApp3D, Print3D, Editorial3D }
  export enum EDerivativeQuality { Thumb, Low, Medium, High, Highest, AR }
  ```

  with `units: TUnitType;` on `IModel` and
  `export type TUnitType = "mm" | "cm" | "m" | "km" | "in" | "ft" | "yd" | "mi";`
  ([Voyager `model.ts`](https://github.com/Smithsonian/dpo-voyager/blob/master/source/client/schema/model.ts),
  [`common.ts`](https://github.com/Smithsonian/dpo-voyager/blob/master/source/client/schema/common.ts)).
- **Counts disagree by time.** **[summary]** About 2,200 models at the 2020 launch; **[opened]**
  Objaverse-XL later counted "a set of 2.4K models, all licensed under a CC0 license"
  ([Objaverse-XL](https://ar5iv.labs.arxiv.org/html/2307.05663)).
- **[inference]** The Smithsonian keeps one measured source per object and publishes derivatives
  keyed by (use, quality), with units declared on the model. That is the same shape as Puck's
  derived products, arrived at for archival reasons.

### Asset stores: Unity and Unreal/Fab

- **Unity Asset Store.** **[opened]** The submission guidelines: "Meshes are at a 1 unit: 1 meter
  scale."; "All meshes have a local pivot point positioned at the bottom center of the GameObject";
  "Assets do not have an unreasonably excessive mesh density or topology that can inhibit mesh
  deformation quality."; "Mesh assets are either .fbx, .dae, .abc, or .obj file types."; review:
  "the Asset Store team checks your incoming submissions against these guidelines during your
  product's review."; AI: "Unity reserves the right to reject submissions that are mass-produced using
  AI"; and for current editors, packages "must support Universal Render Pipeline (URP) or
  High-Definition Render Pipeline (HDRP)"
  ([Unity Asset Store submission guidelines](https://assetstore.unity.com/publishing/submission-guidelines)).
  **[inference]** The render-pipeline clause exists because one asset must be rebuilt per pipeline:
  the asset is coupled to a renderer, and each engine change re-breaks the catalogue.
- **Fab.** **[opened]** "Fab is the successor to the Unreal Engine Marketplace and Sketchfab Store, and
  the new home for Quixel Megascans."; product pages show "if a product was created with the help of
  generative AI." and buyers "can use filters to include or exclude generative AI products from your
  searches." ([Sketchfab blog, Fab launch](https://sketchfab.com/blogs/community/epics-unified-marketplace-fab-launches-today)).
  Fab's licences are "Creative Commons Attribution (CC-BY)" and "Standard", with Standard's Personal
  tier "For buyers who have not generated more than $100,000 USD in gross revenue from commercial
  activity in the last 12 months."; "NoAI meta tags might not be compatible with Creative Commons
  licenses." ([Fab, licenses and pricing](https://dev.epicgames.com/documentation/en-us/fab/licenses-and-pricing-in-fab)).
- **[inference]** Stores keep quality and scale consistent by human review against written rules, and
  they keep licences simple by offering very few. Their failures are operator consolidation (three
  stores folded into Fab) and licences that exist on one store and not its successor.

## Across the libraries

| Library | Licence model | What kept it consistent | Scale rule | What failed |
|---|---|---|---|---|
| Google Poly | CC-BY if remixable, else all rights reserved | Feeder tools (Blocks' low-poly), curation flag | None; orientation hint only | Operator reprioritised; host-only content lost |
| ABO | CC BY 4.0 (paper says BY-NC) | Artists working to one spec | Metres, Y up, +Z front, Y=0 or Z=0 anchors | Paper and page disagree on licence |
| Sketchfab | Per-model CC or Standard | Nothing central | None | Store closed; non-BY CC cannot move to Fab |
| Kenney | CC0 | One author's rule that assets fit | Not stated | None found |
| Quaternius | CC0 per FAQ; QAL per licence page | One author | Not stated | FAQ and licence disagree |
| Thingiverse | Per-design CC | Nothing central | Print units | Terms dispute; attribution stripped by mirrors; Customizer lost |
| Printables | Per-design CC with remix tree | Nothing central | Print units | Late enforcement; licence conflicts in remix trees |
| Objaverse | ODC-By over per-object licences | Filtering after the fact | Rescaled by heuristic | Consent, provenance, quality |
| Smithsonian | CC0 with exclusions | One digitisation office | `units` required | None found |
| Unity / Fab | Store licences; CC-BY on Fab | Human review | Unity: 1 unit = 1 m, bottom-centre pivot | Consolidation; licences lost in migration |

## Lessons for Puck

Each lesson names the general mechanism or failure, then what it becomes in Puck's model. The first
five are the ones that most change Puck's options.

1. **The licence and the credit must be part of the bytes that are identified, and fixed at that
   identity.** Every licence failure above is a licence kept beside the content: Poly's switch lived in
   Google's database, Quaternius's terms depend on when a copy was obtained, ABO's paper and page
   disagree, Sketchfab's NoAI tag cannot follow a CC file, Objaverse's CC labels are uploader claims,
   and Thingiverse's mirrors stripped attribution. **In Puck** a creation's prototype already travels
   under "a full `sha256/` content pin" that "names the compact canonical creation document and mesh"
   ([worlds](../../architecture/worlds.md#observation-and-display)), and a package manifest "holds
   exactly four strings" with no licence or author
   ([asset packages](../../architecture/worlds.md#asset-packages), `WorldPackageManifest`). If the
   licence, the authors and the source pins were members of the canonical creation document, they
   would enter the pin: a mirror could not strip them without changing the identity, and "which terms
   apply" would be answered by the pin itself rather than by the date a copy was obtained. The
   credits for a world would then be derived from its import closure, never kept by hand. Whether
   Puck wants a licence vocabulary at all is an owner call (below).
2. **A library survives its operator only if its content is addressed independently of the host.**
   Poly's salvage worked because the API handed out file URLs and the licence allowed copies; Icosa,
   polygone.art, Poly Pizza and the Internet Archive each became a host. Sketchfab's move to Fab shows
   the same risk with a living operator. **In Puck** content is already fetched against a pin, "its
   bytes must match the pin before they enter the cache", from a store any authority can hold
   ([worlds](../../architecture/worlds.md#observation-and-display)). A Puck library is therefore an
   index of pins with metadata, which any world can host and any other can mirror; the bytes need no
   trusted host. The difference from Poly is structural: closing one index loses a list, while the
   bytes stay usable wherever any store holds them, under the same identity (an inference; nothing
   yet hosts a public index). Note that "A content pin grants no access" (same section): today a prototype body is
   served only to a recipient whose projection names it, so a public library is a new disclosure
   case, not just a new store.
3. **Scale and placement hold only where one door enforces a written convention.** ABO (metres,
   Y up, +Z front, stand on Y=0, hang from Y=0, wall back at Z=0), Unity ("1 unit: 1 meter",
   bottom-centre pivot) and Voyager (mandatory `units`) agree, and the measurement above shows ABO's
   claim holds. Sketchfab had no door and Objaverse had to rescale by heuristic; Poly had only an
   orientation hint. **In Puck** the field is the source, so a creation's extent can be computed from
   the field itself rather than from a mesh, and an anchor kind (stands, hangs, mounts on a wall) can
   be an authored property that the compiler checks against that extent. A declared real-world
   dimension then becomes a checkable claim against the field, the way ABO's listing checks its
   model. Whether Puck creations carry units and anchors today is `puck-creations.md`'s question.
4. **Visual consistency comes from a constrained tool or a single hand, not from moderation.** Blocks'
   low-poly-only modelling, Kenney's rule that "assets fit together", Quaternius as one author and the
   Smithsonian's one digitisation office stay coherent; Sketchfab and Objaverse needed filtering
   afterwards. **In Puck** the constraint is authored: the standard package's palettes, styles and
   templates, which any author "imports, overrides by id, or replaces" ([AGENTS.md](../../../AGENTS.md)).
   A library creation that builds on standard templates and palette roles inherits the look, and a
   world restyles every such creation at once by overriding the palette by id. No mesh library can
   restyle content it does not own; for Puck it is one override. This also keeps the engine generic:
   a library's look is a package's choice, never an engine default.
5. **Expose parameters, and record a remix as its parameters.** Thingiverse's Customizer turned an
   OpenSCAD file's variables into "sliders and radio buttons", and died with the host service that ran
   it; Poly recorded lineage in `RemixInfo` but each remix was a copy the original author "won't be
   able to edit or delete". **In Puck** a creation is a program with parameters, so a library entry is
   a template and a remix is (template pin, parameter values, overrides). The compile is local and
   deterministic, so nothing needs the library's servers to stay up, and lineage is exact rather than
   a copied mesh plus a pointer.
6. **Index cost by measurement, not by declaration.** Poly's `maxComplexity` and `triangleCount` were
   the API's way to find assets an app could afford; Unity can only say "unreasonably excessive";
   ABO's faces run from 20 to 11.5 million (measured above). **In Puck** cost is counted from what the
   author wrote (`world.budget`, creation stats, bake work and bytes), deterministically, so a library
   can rank and filter on measured cost that every host computes identically.
7. **Keep one source and derive the rest, keyed by use.** Poly converted every upload to glTF and USDZ;
   the Smithsonian publishes derivatives by `EDerivativeUsage` and `EDerivativeQuality`; Unity's
   per-pipeline rule shows what happens when the derivative is the shipped artifact. **In Puck** this
   is already the rule: "Shipped derived products (baked meshes and the like) are caches, never the
   truth" ([AGENTS.md](../../../AGENTS.md)). A library hosts creation documents only; thumbnails,
   meshes and previews are derived on demand and keyed from their inputs.
8. **Physical metadata enables uses beyond appearance, but only when it is checked.** ABO's weight,
   material and dimensions are what make it useful to robotics and simulation, and the measurement
   shows retail numbers are approximate. **In Puck** physical parameters (density, material class,
   mass) would be authored properties that simulation consumes deterministically, derived from the
   field where possible (volume from the field, mass from volume and density) rather than typed in
   beside it.

## What Puck should not borrow, and why

- **Mesh formats and a mesh import route.** glTF, OBJ, FBX and USDZ are these libraries' currency, but
  the field is Puck's source, and the hero brief's list of things it rules out includes a "mesh import
  route" ([armored chibi hero brief](../../game/art/armored-chibi-hero-brief.md)). A converter would be a
  second asset pipeline, and the owner's ruling for this round excludes formats.
- **Aggregating other people's work into a corpus.** Objaverse's scale came with consent disputes,
  unverifiable CC labels and after-the-fact filtering; the owner's ruling excludes content.
- **Licence as a label checked at upload.** Printables and Thingiverse check self-declared parents at
  upload time and enforce on report; Puck can derive compatibility from the import closure at compile
  time instead (lesson 1).
- **Popularity as the quality signal.** Poly's `BEST` order and Thingiverse's likes rank what is liked,
  not what is exact or affordable; Puck can measure exactness against the field and cost
  (lessons 3 and 6).
- **Host-run remix services.** Customizer and Poly's in-app remix depended on a live operator; Puck's
  compile is local (lesson 5).
- **Revenue-tier store licences and NoAI meta tags.** Fab's $100,000 tier and the NoAI tag are a
  marketplace's and a platform's answers; Sketchfab itself says the tag "may not be enforceable" for CC
  content. Whether Puck has a marketplace or an AI-use policy is a product decision, not something
  this research settles.
- **Likenesses of real branded products.** ABO's models are Amazon products, and the Smithsonian's
  FAQ excludes trademarks from CC0. A library seeded from real products carries trademark risk
  whatever its copyright licence.

## Open questions

- **Does Puck want a content licence vocabulary, and which?** CC0-only (Kenney, Smithsonian) removes
  every compatibility question; CC-BY (Poly, Fab) needs derived credits; anything wider needs
  compatibility checks over the import closure. Owner decision, raised under **Needs you** in the
  hand-back.
- **Where would licence, authors and source pins live in the canonical creation document, and do they
  enter the content pin?** Owner of the step: the round's synthesis, then a design lane on the
  document model. Check: two creations that differ only in licence have different pins.
- **Do creations carry units and an anchor kind today, and can creation stats report the field's
  extent?** Owner: `puck-creations.md` (lane lib-3) answers what exists; if absent, a step for the
  sdf-authoring area whose check is `puck creation stats` printing a creation's extent from the field.
- **How is a public library served under "A content pin grants no access"?** Owner: a design step for
  the worlds and federation area; it decides whether a library is a world whose projection discloses
  its index's prototypes, which would reuse the existing disclosure path.
- **Unconfirmed facts.** Whether Sketchfab still offers free downloads; whether the Internet Archive's
  Poly item is still restricted; the BleepingComputer original of Google's statement. None changes a
  lesson above.

## Sources

Google Poly:
[launch post](https://blog.google/products/google-VR/poly-browse-discover-and-download-3d-objects-and-scenes/),
[help: shutdown](https://support.google.com/poly/answer/10192635),
[help: supported formats](https://support.google.com/poly/answer/7562662),
[help: uploading](https://support.google.com/poly/answer/9470475?hl=en),
[API: Poly class](https://googleapis.dev/nodejs/googleapis/latest/poly/classes/Poly.html),
[API: Asset](https://googleapis.dev/nodejs/googleapis/latest/poly/interfaces/Schema$Asset.html),
[API: PresentationParams](https://googleapis.dev/nodejs/googleapis/latest/poly/interfaces/Schema$PresentationParams.html),
[API: FormatComplexity](https://googleapis.dev/nodejs/googleapis/latest/poly/interfaces/Schema$FormatComplexity.html),
[API: data classes](https://googleapis.dev/dotnet/Google.Apis.PolyService.v1/latest/api/Google.Apis.PolyService.v1.Data.html),
[9to5Google](https://9to5google.com/2020/12/02/google-poly-shutdown/),
[privacy.com.sg reprint](https://www.privacy.com.sg/?p=10699),
[Wikipedia](https://en.wikipedia.org/wiki/Poly_(website)),
[Archive Team](https://wiki.archiveteam.org/index.php/Google_Poly),
[polygone.art](https://github.com/arodic/polygone.art),
[UploadVR on Icosa](https://www.uploadvr.com/icosa-gallery-beta),
[Icosa about](https://icosa.gallery/about),
[Hugging Face discussion](https://huggingface.co/datasets/allenai/objaverse/discussions/59),
[Tilt Brush source release](https://opensource.googleblog.com/2021/01/the-future-of-tilt-brush.html),
[Blocks source release](https://www.cgchannel.com/2024/08/google-open-sources-virtual-reality-modeling-app-blocks).

Amazon Berkeley Objects:
[dataset page](https://amazon-berkeley-objects.s3.amazonaws.com/index.html),
[README](https://amazon-berkeley-objects.s3.amazonaws.com/README.md),
[3D models README](https://amazon-berkeley-objects.s3.amazonaws.com/3dmodels/README.md),
[paper, arXiv 2110.06199](https://arxiv.org/abs/2110.06199).

Contrasts:
[Sketchfab NoAI and CC](https://sketchfab.com/blogs/community/restricting-generative-ai-use-of-free-models),
[Announcing Fab](https://sketchfab.com/blogs/community/announcing-fab-the-next-phase-for-sketchfab),
[Sketchfab after Fab](https://sketchfab.com/blogs/community/sketchfab-update-what-you-need-to-know-now-that-fabs-live/),
[Fab launch](https://sketchfab.com/blogs/community/epics-unified-marketplace-fab-launches-today),
[80.lv on the migration](https://80.lv/articles/historians-are-concerned-about-epic-games-sketchfab-to-fab-migration),
[Wikipedia, Sketchfab](https://en.wikipedia.org/wiki/Sketchfab),
[3DVF on Objaverse](https://3dvf.com/en/ai-this-massive-dataset-of-3d-objects-is-a-game-change-heres-why-it-may-include-some-of-your-3d-assets/),
[Kenney support](https://kenney.nl/support),
[Kenney kit](https://kenney.nl/assets/modular-space-kit),
[Kenney interview](https://buildbox.com/buildbox-creator-spotlight-kenney-vleugels),
[Quaternius FAQ](https://quaternius.com/faq.html),
[Quaternius licence](https://quaternius.com/license.html),
[Hackaday 2012](https://hackaday.com/2012/09/20/makerbot-occupy-thingiverse-and-the-reality-of-selling-open-hardware),
[Hackaday 2013, Thingiverse](https://hackaday.com/2013/10/02/3d-printering-the-problem-of-thingiverse),
[Hackaday 2013, Customizer](https://hackaday.com/2013/02/16/custom-3d-printed-designs-with-makerbots-customizer),
[MakerBot attribution guide](https://3dprint.com/129999/makerbot-proper-attribution/),
[3DBenchy statements](https://3dprintingindustry.com/news/3dbenchy-gate-prusa-research-and-nti-group-respond-235583),
[3DBenchy public domain](https://3dprintingindustry.com/news/3dbenchy-joins-the-public-domain-a-milestone-for-3d-printing-236377/),
[Hackaday on BY-ND enforcement](https://hackaday.com/tag/creative-commons),
[Objaverse](https://ar5iv.labs.arxiv.org/html/2212.08051),
[Objaverse-XL](https://ar5iv.labs.arxiv.org/html/2307.05663),
[Objaverse++](https://arxiv.org/html/2504.07334v2),
[Smithsonian Open Access FAQ](https://www.si.edu/openaccess/faq),
[Voyager](https://smithsonian.github.io/dpo-voyager/),
[Voyager model schema](https://github.com/Smithsonian/dpo-voyager/blob/master/source/client/schema/model.ts),
[Voyager common schema](https://github.com/Smithsonian/dpo-voyager/blob/master/source/client/schema/common.ts),
[Unity submission guidelines](https://assetstore.unity.com/publishing/submission-guidelines),
[Fab licences](https://dev.epicgames.com/documentation/en-us/fab/licenses-and-pricing-in-fab).
