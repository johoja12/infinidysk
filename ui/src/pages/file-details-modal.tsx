import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { api, body, type Schemas } from "../api/client";
import { Failure } from "../ui";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Details } from "./explorer";
import { Player } from "./player";

export default function FileDetailsModal({ id, onClose, onRestoreFocus }: { id: string; onClose: () => void; onRestoreFocus: () => void }) {
  const client = useQueryClient();
  const [playing, setPlaying] = useState<Schemas["FileEntry"]>();
  const check = useMutation({
    mutationFn: (id: string) => body(api.POST("/api/v1/files/{id}/check", { params: { path: { id } } })),
    onSuccess: () => client.invalidateQueries({ queryKey: ["file", id] }),
  });
  return (
    <>
      <Dialog open={!playing} onOpenChange={(open) => { if (!open) onClose(); }}>
        <DialogContent className="sm:max-w-4xl" onCloseAutoFocus={(event) => { event.preventDefault(); if (!playing) onRestoreFocus(); }}>
          <DialogHeader>
            <DialogTitle>File details</DialogTitle>
            <DialogDescription>Source, health, library links and current cache coverage.</DialogDescription>
          </DialogHeader>
          <Details id={id} embedded onClose={onClose} onRemoved={onClose} actions={{
            play: setPlaying,
            select: () => {},
            check: (item) => check.mutate(item),
          }} />
          {check.error && <Failure error={check.error} />}
          {check.isSuccess && <p role="status">Health check started.</p>}
        </DialogContent>
      </Dialog>
      {playing && <Player entry={playing} onClose={() => setPlaying(undefined)} />}
    </>
  );
}
