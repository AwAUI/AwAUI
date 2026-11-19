import sys
c=[]
def add(*xs):
    for x in xs:
        for t in x.split():
            c.append(t)
# layout
add("sr-only not-sr-only pointer-events-none pointer-events-auto visible invisible collapse static fixed absolute relative sticky")
add("inset-0 inset-x-2 inset-y-1/2 inset-s-4 inset-e-px -inset-2 top-0 top-1/2 -top-4 right-auto bottom-full -left-full left-[3px] top-(--my-top) inset-bs-2 start-4 -end-2 end-1/2 start-auto")
add("isolate isolation-auto z-10 z-50 -z-10 z-auto z-[999] order-1 order-first order-last -order-2 order-none")
add("col-span-2 col-span-full col-start-2 col-end-3 col-auto col-[1/3] row-span-2 row-start-1 row-end-auto row-span-full")
add("float-left float-start clear-both clear-none m-4 mx-auto my-2 -mt-2 mb-px ms-1 me-2 mbs-3 mr-[10px] ml-auto m-0.5 -m-1.5 mt-(--gap)")
add("box-border box-content line-clamp-1 line-clamp-3 line-clamp-none line-clamp-[7]")
add("block inline-block inline hidden flex inline-flex grid inline-grid table table-row table-cell contents list-item flow-root field-sizing-content")
add("aspect-square aspect-video aspect-auto aspect-16/9 aspect-[4/3]")
# sizing
add("size-4 size-full size-1/2 size-[20px] size-auto w-4 w-full w-1/2 w-screen w-fit w-[200px] w-px w-72 w-3xs w-sm w-prose min-w-0 min-w-8 min-w-[8rem] max-w-xs max-w-lg max-w-[calc(100%-2rem)] max-w-none max-w-full max-w-screen-md")
add("h-9 h-px h-full h-screen h-dvh h-auto h-lh h-[1.15rem] h-[var(--radix-select-trigger-height)] min-h-4 min-h-screen max-h-96 max-h-(--radix-dropdown-menu-content-available-height)")
add("inline-full block-4 min-inline-0 max-block-screen container")
add("flex-1 flex-auto flex-none flex-initial flex-[2_2_0%] flex-1/2 shrink shrink-0 grow grow-0 grow-[2] basis-1/3 basis-full basis-64 basis-xs")
add("table-auto table-fixed caption-bottom border-collapse border-separate border-spacing-2 border-spacing-x-1")
add("origin-center origin-top-left origin-(--radix-popover-content-transform-origin) origin-[33%_75%] perspective-dramatic perspective-origin-top")
# transforms
add("translate-x-2 -translate-x-1/2 translate-y-[-50%] translate-y-0.5 translate-full translate-none translate-z-4 translate-3d -translate-y-full translate-x-[calc(100%-2px)] translate-y-[calc(-50%_-_2px)]")
add("scale-95 scale-100 -scale-50 scale-x-75 scale-y-110 scale-z-50 scale-3d scale-[1.7] scale-none")
add("rotate-45 rotate-180 -rotate-90 rotate-[30deg] rotate-x-12 rotate-y-45 -rotate-z-6 rotate-none skew-3 skew-x-6 -skew-y-12")
add("transform transform-gpu transform-cpu transform-none transform-[scale(2)] transform-3d backface-hidden zoom-50")
# interactivity
add("cursor-pointer cursor-default cursor-not-allowed cursor-[url(hand.cur),_pointer] touch-none touch-pan-x touch-pinch-zoom select-none select-text resize resize-none")
add("snap-x snap-mandatory snap-start scroll-mt-4 scroll-my-1 scroll-px-2 scroll-smooth scroll-auto")
add("list-disc list-none list-inside list-image-none appearance-none scheme-dark columns-2 columns-3xs break-after-page break-inside-avoid")
# flex/grid
add("grid-flow-row grid-flow-col-dense auto-cols-fr auto-rows-min auto-rows-[100px] grid-cols-2 grid-cols-12 grid-cols-none grid-cols-[1fr_auto] grid-cols-subgrid grid-rows-[auto_1fr] grid-rows-3")
add("flex-row flex-col flex-col-reverse flex-wrap flex-nowrap place-content-center place-items-start content-between items-center items-baseline-last justify-between justify-center-safe justify-items-start")
add("gap-2 gap-x-6 gap-y-0.5 gap-[3px] gap-(--gap) space-x-4 -space-x-2 space-y-1.5 space-x-reverse space-y-0 space-x-[10px]")
add("place-self-center self-start self-stretch justify-self-end overflow-hidden overflow-x-auto overflow-y-scroll overscroll-contain overscroll-y-none")
add("scrollbar-thin scrollbar-none scrollbar-gutter-stable scrollbar-thumb-red-500 scrollbar-track-gray-100/50")
add("truncate text-ellipsis text-clip hyphens-auto whitespace-nowrap whitespace-pre-wrap tab-4 text-wrap text-balance text-pretty break-all break-keep break-words wrap-anywhere")
# borders
add("rounded rounded-md rounded-lg rounded-full rounded-none rounded-t-lg rounded-s-md rounded-tl-xl rounded-[4px] rounded-[inherit] rounded-xs rounded-(--r)")
add("border border-0 border-2 border-x border-t-4 border-b border-l-[3px] border-solid border-dashed border-none border-red-500 border-red-500/50 border-transparent border-current border-[#fff] border-x-blue-500 border-[length:2px] border-(--c)")
add("divide-x divide-y-2 divide-x-reverse divide-dashed divide-gray-200 divide-[#eee]")
# backgrounds
add("bg-red-500 bg-red-500/50 bg-red-500/[0.3] bg-red-500/(--alpha) bg-black/50 bg-white bg-transparent bg-current bg-inherit bg-[#123456] bg-[rgb(1_2_3)] bg-(--my-color) bg-[url(/img.png)] bg-none bg-cover bg-center bg-no-repeat bg-fixed bg-[length:200px] bg-[position:center_top]")
add("bg-linear-to-r bg-linear-45 -bg-linear-45 bg-linear-to-b/oklch bg-linear-[25deg,red_5%,yellow_60%] bg-radial bg-radial-[at_25%_25%] bg-conic bg-conic-180 bg-gradient-to-r")
add("from-red-500 from-10% via-blue-500 via-30% to-green-500/50 to-90% via-none from-[#fff]")
add("bg-clip-text bg-origin-border bg-blend-multiply mix-blend-overlay")
# masks
add("mask-none mask-[url(/m.png)] mask-alpha mask-cover mask-center mask-no-repeat mask-clip-border mask-t-from-50% mask-b-to-10 mask-x-from-red-500 mask-linear-45 -mask-linear-12 mask-linear-from-20% mask-radial-[100%_100%] mask-radial-from-10 mask-circle mask-radial-at-top mask-conic-90 mask-conic-from-black")
# svg/typography
add("fill-none fill-current fill-red-500 fill-primary stroke-current stroke-2 stroke-[1.5] stroke-blue-500/40 object-cover object-center object-top-left object-[25%_75%]")
add("p-4 px-4 py-2 pt-0 pb-4 pl-8 pr-2 ps-3 pe-1 p-px p-[3px] p-0.5 px-2.5 pbs-2 p-(--pad)")
add("text-left text-center text-start text-end indent-4 -indent-px align-middle align-top align-[4px]")
add("font-sans font-mono font-serif font-medium font-semibold font-bold font-[600] font-['Inter'] font-features-['smcp'] font-stretch-condensed font-stretch-75%")
add("uppercase lowercase capitalize normal-case italic not-italic underline overline line-through no-underline")
add("placeholder-gray-400 placeholder-red-500/50 decoration-red-500 decoration-2 decoration-dashed decoration-from-font decoration-[3px] underline-offset-4 underline-offset-auto -underline-offset-2")
add("animate-spin animate-pulse animate-none animate-ping animate-bounce animate-[wiggle_1s_ease-in-out_infinite]")
add("text-xs text-sm text-base text-lg text-2xl text-9xl text-sm/6 text-sm/[1.5] text-lg/none text-[14px] text-[14px]/7 text-[length:var(--x)] text-red-500 text-red-500/75 text-white text-current text-[#333] text-[color:var(--c)] text-(--c)")
add("leading-none leading-tight leading-6 leading-[1.2] tracking-tight tracking-widest -tracking-wide tracking-[0.1em] antialiased subpixel-antialiased")
add("ordinal slashed-zero tabular-nums diagonal-fractions normal-nums")
add("text-shadow-sm text-shadow-lg text-shadow-none text-shadow-red-500 text-shadow-lg/50 text-shadow-[0_1px_2px_black] text-shadow-initial")
# filters
add("filter filter-none blur blur-sm blur-none blur-[2px] brightness-50 contrast-125 grayscale grayscale-0 hue-rotate-90 -hue-rotate-15 invert saturate-150 sepia")
add("backdrop-blur-sm backdrop-blur backdrop-filter backdrop-brightness-110 backdrop-opacity-50 backdrop-grayscale backdrop-invert-0 backdrop-sepia-0 backdrop-saturate-100 backdrop-contrast-75")
add("drop-shadow drop-shadow-md drop-shadow-none drop-shadow-red-500 drop-shadow-lg/50 drop-shadow-[0_35px_35px_rgba(0,0,0,0.25)]")
# transitions
add("transition transition-all transition-colors transition-opacity transition-shadow transition-transform transition-none transition-[color,box-shadow] transition-[width] transition-discrete")
add("duration-200 duration-[1s] duration-initial delay-150 delay-[2s] ease-in-out ease-linear ease-out ease-[cubic-bezier(0.95,0.05,0.795,0.035)] ease-initial")
add("will-change-transform will-change-[top,left] content-none content-['hello'] content-[attr(data-x)] contain-paint contain-size contain-none forced-color-adjust-none")
# outline/opacity
add("outline outline-0 outline-2 outline-none outline-hidden outline-dashed outline-red-500 outline-ring/50 outline-offset-2 -outline-offset-1 outline-[3px] opacity-0 opacity-50 opacity-[.67] opacity-2.5")
# shadows/rings
add("shadow shadow-xs shadow-sm shadow-md shadow-lg shadow-none shadow-red-500 shadow-red-500/50 shadow-lg/20 shadow-[0_0_10px_red] shadow-[0_35px_60px_-15px_rgba(0,0,0,0.3)] shadow-inherit shadow-initial")
add("inset-shadow-sm inset-shadow-none inset-shadow-red-500 inset-shadow-[0_1px_2px_black] inset-shadow-2xs/50")
add("ring ring-0 ring-2 ring-[3px] ring-red-500 ring-red-500/50 ring-inset ring-offset-2 ring-offset-white ring-[#fff] inset-ring inset-ring-2 inset-ring-red-500")
add("@container @container/main @container-normal @container-size")
# arbitrary properties
add("[mask-type:luminance] [--my-var:10px] [color:red] [--scroll-offset:56px] [mask-image:linear-gradient(black,transparent)]")
# important & negatives
add("!p-4 p-4! !text-red-500 hover:!bg-red-500 hover:bg-red-500! -mx-2 -translate-y-1/2")
# variants
V="hover focus focus-visible focus-within active disabled checked first last odd even only placeholder file selection marker before after backdrop first-letter first-line open visited target default indeterminate placeholder-shown autofill optional required valid invalid user-valid user-invalid in-range out-of-range read-only empty enabled inert motion-safe motion-reduce contrast-more print portrait landscape ltr rtl dark starting forced-colors pointer-fine pointer-coarse any-pointer-coarse noscript inverted-colors details-content"
for v in V.split(): c.append(v+":flex")
add("sm:flex md:grid lg:hidden xl:block 2xl:inline max-sm:hidden max-md:flex min-[600px]:flex max-[900px]:hidden md:max-lg:flex sm:hover:bg-red-500")
add("@sm:flex @md:grid @lg/main:hidden @max-md:block @min-[400px]:flex @[400px]:flex @max-[300px]:flex")
add("group-hover:flex group-focus:underline group-hover/item:flex group-data-[state=open]:rotate-180 group-[.is-published]:block group-has-[a]:block peer-checked:bg-blue-500 peer-hover/name:flex peer-[.x]:flex peer-disabled:opacity-50")
add("has-[>svg]:px-3 has-checked:bg-red-500 has-[:focus]:ring-2 has-data-[slot=card-action]:grid-cols-[1fr_auto] has-aria-expanded:bg-muted")
add("not-first:mt-2 not-hover:opacity-50 not-[.x]:flex not-sm:hidden not-supports-[display:grid]:flex not-dark:flex not-data-[state=open]:hidden not-has-[svg]:p-2")
add("in-focus:flex in-data-[state=open]:flex in-[.dark]:text-white")
add("aria-checked:bg-blue-500 aria-[sort=ascending]:flex aria-invalid:border-red-500 aria-expanded:rotate-180 aria-[invalid=true_i]:flex")
add("data-[state=open]:flex data-active:flex data-[size=sm]:text-xs data-[orientation=vertical]:h-full data-disabled:opacity-50 data-[x='y']:flex")
add("nth-3:flex nth-[2n+1]:flex nth-last-2:flex nth-of-type-2:flex nth-last-of-type-[3]:flex")
add("supports-[display:grid]:grid supports-grid:grid supports-[selector(:has(a))]:flex supports-backdrop-filter:bg-red-500/50")
add("*:flex **:flex *:data-[slot=avatar]:ring-2 *:[span]:last:flex")
add("[&_svg]:size-4 [&>svg]:size-3 [&_svg:not([class*='size-'])]:size-4 [.border-b]:pb-6 [a&]:hover:underline [&[data-state=open]>svg]:rotate-180 [&:has([role=checkbox])]:pr-0 [[data-slot=tooltip-content]_&]:bg-white [@media(min-width:900px)]:flex [@supports(display:grid)]:grid [&.is-dragging]:cursor-grabbing")
add("dark:hover:bg-red-500 dark:md:hover:focus:bg-red-500 hover:focus:underline focus:hover:underline before:content-['*'] after:absolute placeholder:text-gray-400 file:border-0 selection:bg-blue-500 marker:text-red-500 first-letter:text-2xl")
add("group-hover:group-focus:flex peer-checked:peer-hover:flex group-aria-checked:flex group-not-first:flex")
# invalid-ish (expected none)
add("foo bg-unknown-color p-unknown hover:foo unknownvariant:flex text-red-500/xyz w-[;] p-[ ] [color]:red")
# misc
add("w-[calc(100%-theme(spacing.4))] w-[calc(100%-var(--x))] grid-cols-[repeat(auto-fill,minmax(200px,1fr))] h-[calc(100vh-4rem)] w-[min(100%,500px)] translate-x-[max(1rem,10%)]")
add("bg-[var(--a)] bg-[color:var(--a)] text-[var(--size)] text-[length:var(--size)] w-[--spacing(4)]")
seen=set();out=[]
for x in c:
    if x not in seen: seen.add(x); out.append(x)
open(sys.argv[1],'w').write('\n'.join(out)+'\n')
print(len(out))
