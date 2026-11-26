import { Show, createSignal, onCleanup, onMount } from 'solid-js';
import { SpinePlayer } from '@esotericsoftware/spine-player';
import '@esotericsoftware/spine-player/dist/spine-player.css';

export function AronaSpine(props: { class?: string; onInteract?: () => void }) {
  let host!: HTMLDivElement;
  const [failed, setFailed] = createSignal(false);
  let player: SpinePlayer | undefined;
  let idleAnim = '';
  let reactAnims: string[] = [];
  let reactIdx = 0;

  onMount(() => {
    try {
      player = new SpinePlayer(host, {
        binaryUrl: '/arona-spine/arona_workpage_daytime_2.skel',
        atlasUrl: '/arona-spine/arona_workpage_daytime_2.atlas',
        alpha: true,
        backgroundColor: '#00000000',
        premultipliedAlpha: false,
        showControls: false,
        showLoading: false,
        interactive: false,
        defaultMix: 0.25,
        preserveDrawingBuffer: false,
        viewport: { padLeft: '2%', padRight: '2%', padTop: '4%', padBottom: '0%' },
        success: (p) => {
          const anims = p.skeleton?.data.animations.map((a) => a.name) ?? [];
          idleAnim = anims.find((n) => /idle/i.test(n)) ?? anims[0] ?? '';
          reactAnims = anims.filter((n) => n !== idleAnim && !/look|start/i.test(n));
          if (reactAnims.length === 0) reactAnims = anims.filter((n) => n !== idleAnim);
          if (idleAnim) p.setAnimation(idleAnim, true);
        },
        error: () => setFailed(true),
      });
    } catch {
      setFailed(true);
    }
  });

  onCleanup(() => {
    player?.dispose();
  });

  const interact = () => {
    if (player?.animationState && reactAnims.length > 0) {
      const next = reactAnims[reactIdx % reactAnims.length];
      reactIdx += 1;
      player.animationState.setAnimation(0, next, false);
      if (idleAnim) player.animationState.addAnimation(0, idleAnim, true, 0);
    }
    props.onInteract?.();
  };

  return (
    <Show
      when={!failed()}
      fallback={
        <img
          src="/game-ui/Image_Char_Arona.png"
          alt="Arona"
          onClick={interact}
          class="size-32 cursor-pointer rounded-full border-4 border-white shadow-lg transition hover:scale-105"
        />
      }
    >
      <div
        ref={host}
        onClick={interact}
        class={props.class ?? 'h-[210px] w-[250px] shrink-0 cursor-pointer select-none overflow-hidden rounded-xl'}
      />
    </Show>
  );
}
