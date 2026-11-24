import type { SVGProps } from "react";

export function SelfrunMark(props: SVGProps<SVGSVGElement>) {
  return (
    <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32" fill="none" {...props}>
      <path d="M8 7h16v4H12v2.5h5L13.5 17H8V7z" fill="currentColor" />
      <path d="M24 25H8v-4h12v-2.5h-5L18.5 15H24v10z" fill="currentColor" />
    </svg>
  );
}
