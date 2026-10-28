;;; TYPC Toolbox - AutoCAD 2016 loader
;;; DLL : TYPC.dll (v1.0.0.0, acdbmgd/acmgd 20.1 = AutoCAD 2016)
;;;       System.ValueTuple.dll must sit next to TYPC.dll (used by BBLK clustering).
;;; Usage: APPLOAD this file, or drag it into the drawing area.
;;; Auto-load: APPLOAD -> Startup Suite (Contents...) -> add this file.

(defun TYPC-LOAD (/ dll dep)
  (setq dll "D:\\ai\\TYPC\\dist\\AutoCAD2016\\TYPC.dll")
  (setq dep "D:\\ai\\TYPC\\dist\\AutoCAD2016\\System.ValueTuple.dll")
  (if (not (findfile dep))
    (princ "\n[TYPC] WARNING - System.ValueTuple.dll missing, BBLK may fail."))
  (if (findfile dll)
    (progn
      (command "_.NETLOAD" dll)
      (princ (strcat "\n[TYPC] loaded: " dll))
      (princ "\n[TYPC] TYPC / TYY / THUI - open toolbox panel")
      (princ "\n[TYPC] THBH / THSZ / THCS / THBJ - drawing number tools")
      (princ "\n[TYPC] BBLK / BATCHBLOCK / SELBLK - batch make blocks")
      (princ "\n[TYPC] WZXX / TAXX - text & pattern linetype")
      (princ "\n[TYPC] WZQF - text prefix/suffix"))
    (princ (strcat "\n[TYPC] ERROR - file not found: " dll)))
  (princ))

(TYPC-LOAD)
(princ)
